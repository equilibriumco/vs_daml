using System.Collections.Generic;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageClient;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

public class DamlMiddleLayerTests
{
    private const string Inside = "file:///M:/repo/daml/Main.daml";
    private const string Outside = "file:///M:/elsewhere/keywords.daml";

    private static DamlMiddleLayer Layer() => new(NullDamlLogger.Instance, uri => uri != Outside);

    private static JObject Params(string uri) => JObject.FromObject(new { textDocument = new { uri }, position = new { line = 0, character = 0 } });

    [Fact, Trait("Category", "Editor")]
    public void Every_document_message_is_looked_at() =>
        Layer().CanHandle("textDocument/didOpen").ShouldBeTrue();

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

    [Fact, Trait("Category", "Editor")]
    public async Task Requests_about_workspace_files_reach_the_server()
    {
        var answer = new JObject { ["contents"] = "x" };

        var result = await Layer().HandleRequestAsync("textDocument/hover", Params(Inside), p => Task.FromResult<JToken?>(answer));

        result.ShouldBeSameAs(answer);
    }
}
