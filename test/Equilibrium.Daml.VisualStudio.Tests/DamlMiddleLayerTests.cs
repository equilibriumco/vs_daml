using System.Collections.Generic;
using System.Threading.Tasks;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageClient;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Tests;

public class DamlMiddleLayerTests
{
    private const string Inside = "file:///M:/repo/daml/Main.daml";
    private const string Outside = "file:///M:/elsewhere/keywords.daml";

    private static DamlMiddleLayer Layer() => new(NullDamlLogger.Instance, uri => uri != Outside);

    private static JObject Params(string uri) => JObject.FromObject(new { textDocument = new { uri }, position = new { line = 0, character = 0 } });

    [Theory, Trait("Category", "Editor")]
    [InlineData("textDocument/didOpen", true)]
    [InlineData("textDocument/didClose", true)]
    [InlineData("textDocument/hover", true)]
    [InlineData("textDocument/definition", true)]
    [InlineData("workspace/didChangeConfiguration", true)]
    [InlineData("initialize", false)]
    [InlineData("workspace/symbol", false)]
    [InlineData("daml/keepAlive", false)]
    public void Every_document_message_is_looked_at_and_nothing_else(string method, bool handled) =>
        Layer().CanHandle(method).ShouldBe(handled, method);

    // With Debug on, every message passes through, to be logged.
    [Theory, Trait("Category", "Editor")]
    [InlineData("initialize")]
    [InlineData("workspace/symbol")]
    public void With_debug_logging_every_message_is_looked_at(string method) =>
        new DamlMiddleLayer(new RecordingLogger(DamlLogLevel.Debug), _ => true).CanHandle(method).ShouldBeTrue(method);

    [Fact, Trait("Category", "Editor")]
    public async Task Notifications_about_outside_files_are_dropped()
    {
        var sent = new List<string>();
        var layer = Layer();

        await layer.HandleNotificationAsync("textDocument/didOpen", Params(Outside), p => { sent.Add("outside"); return Task.CompletedTask; });
        await layer.HandleNotificationAsync("textDocument/didOpen", Params(Inside), p => { sent.Add("inside"); return Task.CompletedTask; });

        sent.ShouldBe(["inside"]);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Requests_about_outside_files_get_an_empty_answer_without_the_server()
    {
        var asked = false;

        var result = await Layer().HandleRequestAsync("textDocument/hover", Params(Outside), p =>
        {
            asked = true;
            return Task.FromResult<JToken?>(new JObject());
        });

        asked.ShouldBeFalse();
        result.ShouldBeNull();
    }

    // damlc has no handler for it and logs an error each time; settings reach it on its command line.
    [Fact, Trait("Category", "Editor")]
    public async Task Configuration_changes_are_kept_from_the_server()
    {
        var sent = false;
        var layer = Layer();

        layer.CanHandle("workspace/didChangeConfiguration").ShouldBeTrue();
        await layer.HandleNotificationAsync("workspace/didChangeConfiguration", JObject.FromObject(new { settings = new { daml = new { } } }), p =>
        {
            sent = true;
            return Task.CompletedTask;
        });

        sent.ShouldBeFalse();
    }

    // Library sources live under paths too long for Visual Studio; their answers are redirected.
    [Theory, Trait("Category", "Editor")]
    [InlineData("textDocument/definition", true)]
    [InlineData("textDocument/declaration", true)]
    [InlineData("textDocument/typeDefinition", true)]
    [InlineData("textDocument/implementation", true)]
    [InlineData("textDocument/hover", false)]
    public async Task Definition_answers_go_through_the_redirect(string method, bool redirected)
    {
        var answer = new JObject { ["uri"] = "file:///C:/long/Library.daml" };
        var replaced = new JObject { ["uri"] = "file:///C:/short/Library.daml" };
        var layer = new DamlMiddleLayer(NullDamlLogger.Instance, _ => true, result => replaced);

        var result = await layer.HandleRequestAsync(method, Params(Inside), p => Task.FromResult<JToken?>(answer));

        result.ShouldBeSameAs(redirected ? replaced : answer);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Requests_about_workspace_files_reach_the_server()
    {
        var answer = new JObject { ["contents"] = "x" };

        var result = await Layer().HandleRequestAsync("textDocument/hover", Params(Inside), p => Task.FromResult<JToken?>(answer));

        result.ShouldBeSameAs(answer);
    }
    [Theory, Trait("Category", "Editor")]
    [InlineData("textDocument/hover")]
    [InlineData("textDocument/definition")]
    public async Task Library_requests_are_mapped_before_scope_checks_and_forwarding(string method)
    {
        var layer = new DamlMiddleLayer(NullDamlLogger.Instance, uri => uri == Inside, originalDocument: parameters => Params(Inside));
        string? requestedUri = null;
        await layer.HandleRequestAsync(method, Params(Outside), parameters =>
        {
            requestedUri = (string?)parameters["textDocument"]?["uri"];
            return Task.FromResult<JToken?>(new JObject());
        });
        requestedUri.ShouldBe(Inside);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Library_open_and_close_notifications_use_the_original_uri()
    {
        var layer = new DamlMiddleLayer(NullDamlLogger.Instance, uri => uri == Inside, originalDocument: parameters => Params(Inside));
        var sent = new List<string>();
        foreach (var method in new[] { "textDocument/didOpen", "textDocument/didClose" })
        {
            await layer.HandleNotificationAsync(method, Params(Outside), parameters =>
            {
                sent.Add((string)parameters["textDocument"]!["uri"]!);
                return Task.CompletedTask;
            });
        }
        sent.ShouldBe([Inside, Inside]);
    }

}
