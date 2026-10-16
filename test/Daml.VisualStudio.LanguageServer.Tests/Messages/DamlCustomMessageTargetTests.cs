using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Messages;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Messages;

public sealed class DamlCustomMessageTargetTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly RecordingSinks _sinks = new();

    private DamlCustomMessageTarget Target() => new(_sinks, _sinks, NullDamlLogger.Instance);

    private static JObject Notification(string method, object parameters) =>
        new() { ["method"] = method, ["params"] = JObject.FromObject(parameters) };

    // damlc's own notifications, sent by the fake server the way damlc sends them, arrive typed.
    [Fact, Trait("Category", "Process")]
    public async Task Every_daml_notification_reaches_its_sink_with_its_fields()
    {
        var scenario = new FakeLanguageServerScenario();
        scenario.NotificationsAfterInitialized.AddRange(
        [
            Notification("daml/virtualResource/didProgress", new { uri = "daml://compiler?file=a", millisecondsPassed = 1200, startedAt = 1760000000000 }),
            Notification("daml/virtualResource/didChange", new { uri = "daml://compiler?file=a", contents = "<table/>" }),
            Notification("daml/virtualResource/note", new { uri = "daml://compiler?file=a", note = "Script failed" }),
            Notification("daml/sdkInstallProgress", new { sdkVersionIdentifier = "3.6.0", sdkVersionRendered = "SDK 3.6.0", kind = "begin", progress = 0 }),
            Notification("daml/sdkInstallProgress", new { sdkVersionIdentifier = "3.6.0", sdkVersionRendered = "SDK 3.6.0", kind = "report", progress = 40 }),
            Notification("daml/sdkInstallProgress", new { sdkVersionIdentifier = "3.6.0", sdkVersionRendered = "SDK 3.6.0", kind = "end", progress = 100 }),
        ]);
        using var server = FakeLanguageServer.Create(scenario);
        using var process = new ProcessRunner().Start(new ProcessSpec(server.ExecutablePath, []) { Environment = server.Environment.ToDictionary(p => p.Key, p => (string?)p.Value) });
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(process.StandardInput, process.StandardOutput, new JsonMessageFormatter()));
        rpc.AddLocalRpcTarget(Target());
        rpc.StartListening();

        await rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, capabilities = new { } });
        await rpc.NotifyWithParameterObjectAsync("initialized", new { });
        await _sinks.WaitForAsync(6, Generous);

        _sinks.Progress.ShouldHaveSingleItem().ShouldBe(new VirtualResourceProgressParams("daml://compiler?file=a", 1200, 1760000000000));
        _sinks.Changes.ShouldHaveSingleItem().ShouldBe(new VirtualResourceChangedParams("daml://compiler?file=a", "<table/>"));
        _sinks.Notes.ShouldHaveSingleItem().ShouldBe(new VirtualResourceNoteParams("daml://compiler?file=a", "Script failed"));
        _sinks.Installs.Select(i => (i.Kind, i.Progress)).ShouldBe([(SdkInstallProgressKind.Begin, 0), (SdkInstallProgressKind.Report, 40), (SdkInstallProgressKind.End, 100)]);
        _sinks.Installs.ShouldAllBe(i => i.SdkVersionIdentifier == "3.6.0" && i.SdkVersionRendered == "SDK 3.6.0");
        await rpc.NotifyAsync("exit");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("""{"contents":"no uri"}""")]
    [InlineData("""{"uri":"daml://x"}""")]
    [InlineData("[]")]
    public void A_malformed_notification_is_ignored(string json)
    {
        var target = Target();

        target.VirtualResourceDidChange(JToken.Parse(json));
        target.SdkInstallProgress(JToken.Parse(json));

        _sinks.Count.ShouldBe(0);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_unknown_install_kind_is_ignored()
    {
        Target().SdkInstallProgress(JObject.FromObject(new { sdkVersionIdentifier = "3.6.0", sdkVersionRendered = "SDK 3.6.0", kind = "pause", progress = 1 }));

        _sinks.Count.ShouldBe(0);
    }

    [Fact, Trait("Category", "Process")]
    public async Task Cancelling_an_install_is_sent_to_the_server()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        using var process = new ProcessRunner().Start(new ProcessSpec(server.ExecutablePath, []) { Environment = server.Environment.ToDictionary(p => p.Key, p => (string?)p.Value) });
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(process.StandardInput, process.StandardOutput, new JsonMessageFormatter()));
        rpc.StartListening();
        await rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, capabilities = new { } });
        var channel = new DamlRpcChannel();
        channel.Attach(rpc, 1);

        (await channel.CancelSdkInstallAsync("3.6.0")).ShouldBeTrue();

        var deadline = DateTime.UtcNow + Generous;
        while (!server.ReadReceivedMethods().Contains("daml/sdkInstallCancel:3.6.0") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        server.ReadReceivedMethods().ShouldContain("daml/sdkInstallCancel:3.6.0");
        await rpc.NotifyAsync("exit");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_a_session_there_is_nothing_to_cancel()
    {
        (await new DamlRpcChannel().CancelSdkInstallAsync("3.6.0")).ShouldBeFalse();
    }

    private sealed class RecordingSinks : IVirtualResourceSink, ISdkInstallProgressSink
    {
        private readonly object _gate = new();

        public List<VirtualResourceChangedParams> Changes { get; } = new();

        public List<VirtualResourceNoteParams> Notes { get; } = new();

        public List<VirtualResourceProgressParams> Progress { get; } = new();

        public List<SdkInstallProgressParams> Installs { get; } = new();

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return Changes.Count + Notes.Count + Progress.Count + Installs.Count;
                }
            }
        }

        public void Changed(VirtualResourceChangedParams change) => Add(Changes, change);

        public void Noted(VirtualResourceNoteParams note) => Add(Notes, note);

        public void Progressed(VirtualResourceProgressParams progress) => Add(Progress, progress);

        public void Report(SdkInstallProgressParams progress) => Add(Installs, progress);

        public async Task WaitForAsync(int count, TimeSpan limit)
        {
            var deadline = DateTime.UtcNow + limit;
            while (Count < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Count.ShouldBe(count);
        }

        private void Add<T>(List<T> list, T item)
        {
            lock (_gate)
            {
                list.Add(item);
            }
        }
    }
}
