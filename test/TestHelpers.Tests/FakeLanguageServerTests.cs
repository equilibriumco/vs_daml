using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Equilibrium.Daml.TestHelpers.Tests;

public class FakeLanguageServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact, Trait("Category", "Process")]
    public async Task Initialize_returns_the_scripted_capabilities()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario
        {
            Capabilities = new JObject { ["hoverProvider"] = true, ["definitionProvider"] = true },
        });
        using var session = server.Start();

        var result = await session.Rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = (int?)null, rootUri = (string?)null, capabilities = new { } });

        result["capabilities"]!["hoverProvider"]!.Value<bool>().ShouldBeTrue();
        result["capabilities"]!["definitionProvider"]!.Value<bool>().ShouldBeTrue();
    }

    [Fact, Trait("Category", "Process")]
    public async Task Clean_shutdown_exits_with_zero_and_records_the_conversation()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        using var session = server.Start();

        await session.Rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { capabilities = new { } });
        await session.Rpc.NotifyWithParameterObjectAsync("initialized", new { });
        await session.Rpc.InvokeAsync<object?>("shutdown");
        await session.Rpc.NotifyAsync("exit");

        (await session.WaitForExitAsync(Timeout)).ShouldBe(0);
        server.ReadReceivedMethods().ShouldBe(new[] { "initialize", "initialized", "shutdown", "exit" });
    }

    [Fact, Trait("Category", "Process")]
    public async Task Exit_without_shutdown_exits_with_one()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        using var session = server.Start();

        await session.Rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { capabilities = new { } });
        await session.Rpc.NotifyAsync("exit");

        (await session.WaitForExitAsync(Timeout)).ShouldBe(1);
    }

    [Fact, Trait("Category", "Process")]
    public async Task Closing_stdin_ends_the_process()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        using var session = server.Start();

        session.CloseInput();

        (await session.WaitForExitAsync(Timeout)).ShouldBe(1);
    }

    private sealed class ClientSide
    {
        public List<JObject> Published { get; } = [];

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(JToken parameters)
        {
            lock (Published)
            {
                Published.Add((JObject)parameters);
            }
        }

        public List<JToken> ServerRequests { get; } = [];

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public object? RegisterCapability(JToken parameters) => Note(parameters);

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object? CreateProgress(JToken parameters) => Note(parameters);

        private object? Note(JToken parameters)
        {
            lock (ServerRequests)
            {
                ServerRequests.Add(parameters);
            }
            return null;
        }
    }

    // A transcript recorded against the real damlc answers the same requests the same way, so a
    // test can run the client against yesterday's server without the server.
    [Fact, Trait("Category", "Process")]
    public async Task A_recorded_transcript_answers_like_the_real_server()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario
        {
            Replay = FakeLanguageServerScenario.Transcript("3.6.0", "ide-A_type_error_is_published_for_the_opened_file"),
            ReplayRoot = "C:/work/app",
        });
        var client = new ClientSide();
        var published = client.Published;
        using var session = server.Start(client);

        var initialized = await session.Rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, rootUri = "file:///C:/work/app", capabilities = new { } });
        JToken.DeepEquals(initialized["capabilities"], FakeLanguageServerScenario.RecordedCapabilities("ide-3.6.0")).ShouldBeTrue();
        await session.Rpc.NotifyWithParameterObjectAsync("initialized", new { });
        await session.Rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new { textDocument = new { uri = "file:///C:/work/app/daml/TypeError.daml", languageId = "daml", version = 1, text = "module TypeError where" } });

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (published)
            {
                if (published.Any(p => ((string?)p["uri"] ?? "").Contains("TypeError.daml") && p["diagnostics"] is JArray { Count: > 0 }))
                {
                    break;
                }
            }
            await Task.Delay(50);
        }
        lock (published)
        {
            var diagnostics = published.Last(p => ((string?)p["uri"] ?? "").Contains("TypeError.daml"))["diagnostics"]!;
            ((string)diagnostics[0]!["message"]!).ShouldContain("Couldn't match expected type");
        }
        await session.Rpc.InvokeAsync<object?>("shutdown");
        await session.Rpc.NotifyAsync("exit");
        (await session.WaitForExitAsync(Timeout)).ShouldBe(0);
    }
}
