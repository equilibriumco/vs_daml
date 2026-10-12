using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.LanguageServer.Messages;
using Daml.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Hosting;

// The watchdog's request, exactly as it is sent, must get an answer from the real damlc;
// otherwise every healthy server would be restarted after two minutes.
public class RealDamlcKeepAliveContractTests
{
    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("multi-ide")]
    [InlineData("ide")]
    public async Task Damlc_answers_keep_alive(string mode)
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var package = Repository.Sample("single-package");
        using var process = new ProcessRunner().Start(new ProcessSpec(dpm, ["damlc", mode]) { WorkingDirectory = package });
        var stderr = process.StandardError.CopyToAsync(Stream.Null);
        var settings = new LspAdapterSettings();
        var tracker = new InitializeTracker();
        var documents = new OpenDocuments();
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            new ClientToServerStream(process.StandardInput, settings, tracker, NullDamlLogger.Instance, documents),
            new ServerToClientStream(process.StandardOutput, settings, tracker, NullDamlLogger.Instance, documents),
            new JsonMessageFormatter()));
        rpc.AddLocalRpcMethod("window/workDoneProgress/create", new Func<JToken, object?>(_ => null));
        rpc.AddLocalRpcMethod("client/registerCapability", new Func<JToken, object?>(_ => null));
        rpc.StartListening();
        var initialize = rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, rootUri = new Uri(package).AbsoluteUri, capabilities = new { } });
        (await Task.WhenAny(initialize, Task.Delay(TimeSpan.FromMinutes(2)))).ShouldBe(initialize, "damlc didn't answer initialize");
        await rpc.NotifyWithParameterObjectAsync("initialized", new { });

        for (var i = 0; i < 2; i++)
        {
            var ping = rpc.KeepAliveAsync(CancellationToken.None);
            (await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(ping, $"damlc {mode} didn't answer daml/keepAlive");
            await ping;
        }

        await rpc.NotifyAsync("exit");
        GC.KeepAlive(stderr);
    }
}
