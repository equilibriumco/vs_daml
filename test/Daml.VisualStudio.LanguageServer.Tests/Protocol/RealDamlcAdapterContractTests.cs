using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Protocol;

// damlc exits on the first enum value it doesn't know. Through the adapter, an initialize shaped
// like VS's (private completion kinds and diagnostic tags) must get a reply instead.
public class RealDamlcAdapterContractTests
{
    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("multi-ide")]
    [InlineData("ide")]
    public async Task Damlc_answers_a_VS_shaped_initialize_sent_through_the_adapter(string mode)
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var spec = new ProcessSpec(dpm, ["damlc", mode]) { WorkingDirectory = Repository.Sample("single-package") };
        using var process = new ProcessRunner().Start(spec);
        // damlc logs to stderr; left unread, the pipe fills up and the server stalls.
        var stderr = process.StandardError.CopyToAsync(Stream.Null);
        var settings = new LspAdapterSettings();
        var tracker = new InitializeTracker();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Initialized += (_, _) => ready.TrySetResult(true);
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            new ClientToServerStream(process.StandardInput, settings, tracker, NullDamlLogger.Instance),
            new ServerToClientStream(process.StandardOutput, settings, tracker, NullDamlLogger.Instance),
            new JsonMessageFormatter()));
        rpc.StartListening();
        var initialize = JObject.Parse("""
            {"processId":1,"rootUri":null,"capabilities":{"textDocument":{
              "completion":{"completionItemKind":{"valueSet":[0,1,2,3,25,118115,118126]}},
              "publishDiagnostics":{"tagSupport":{"valueSet":[1,2,2147483645,2147483646]}}}}}
            """);

        // A cancelled JSON-RPC request still waits for the server, so time out from outside.
        var reply = rpc.InvokeWithParameterObjectAsync<JObject>("initialize", initialize);
        (await Task.WhenAny(reply, Task.Delay(TimeSpan.FromMinutes(2)))).ShouldBe(reply, "damlc didn't answer initialize");

        (await reply)["capabilities"].ShouldNotBeNull();
        await rpc.NotifyWithParameterObjectAsync("initialized", new { });
        (await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBe(ready.Task);
        process.Exited.IsCompleted.ShouldBeFalse("damlc exited instead of serving");
        await rpc.NotifyAsync("exit");
        GC.KeepAlive(stderr);
    }
}
