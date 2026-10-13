using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

namespace Daml.VisualStudio.LanguageServer.Tests.Protocol;

// damlc (3.6.2, ide and multi-ide) stops serving a document that is closed and opened again: no
// diagnostics, no answers, not even after edits. Visual Studio closes a document whenever its tab
// closes, so the adapter keeps the close from damlc. These tests talk to the real damlc the way
// Visual Studio does, through the adapter.
public class RealDamlcReopenContractTests
{
    private readonly ITestOutputHelper _output;

    public RealDamlcReopenContractTests(ITestOutputHelper output) => _output = output;

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("multi-ide")]
    [InlineData("ide")]
    public async Task A_reopened_file_gets_its_diagnostics_back_and_is_still_served(string mode)
    {
        var package = Repository.Sample("broken");
        var file = Path.Combine(package, "daml", "TypeError.daml");
        var uri = new Uri(file).AbsoluteUri;
        await using var session = await StartAsync(mode, package, uri);
        var rpc = session.Rpc;
        var published = session.Published;
        var text = File.ReadAllText(file);
        object Opened(int version) => new { textDocument = new { uri, languageId = "daml", version, text } };

        await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", Opened(1));
        (await WaitForAsync(published, 0, count => count > 0)).ShouldBeTrue("no diagnostics after the first open");
        await rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri } });
        await Task.Delay(TimeSpan.FromSeconds(2));
        var beforeReopen = Snapshot(published).Count;

        await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", Opened(2));
        (await WaitForAsync(published, beforeReopen, count => count > 0)).ShouldBeTrue("the error didn't come back after the reopen");

        // Fixing the error must clear it: proof that damlc still serves the file.
        var afterReopen = Snapshot(published).Count;
        var fixedText = text.Replace("\"forty-two\"", "42");
        fixedText.ShouldNotBe(text);
        await rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new { textDocument = new { uri, version = 3 }, contentChanges = new[] { new { text = fixedText } } });
        var cleared = await WaitForAsync(published, afterReopen, count => count == 0);

        _output.WriteLine($"{mode}: diagnostics counts in order: {string.Join(", ", Snapshot(published))}");
        cleared.ShouldBeTrue("damlc didn't react to an edit after the reopen");
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("multi-ide")]
    [InlineData("ide")]
    public async Task Reopening_with_shorter_fixed_text_clears_errors_and_returns_hover_ranges_in_the_new_text(string mode)
    {
        var package = Repository.Sample("broken");
        var file = Path.Combine(package, "daml", "TypeError.daml");
        var uri = new Uri(file).AbsoluteUri;
        await using var session = await StartAsync(mode, package, uri);
        await session.Rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "daml", version = 1, text = File.ReadAllText(file) } });
        (await WaitForAsync(session.Published, 0, count => count > 0)).ShouldBeTrue();
        await session.Rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri } });
        var before = Snapshot(session.Published).Count;
        const string ReopenedText = "module TypeError where\n\ntotal : Int\ntotal = 42\n";
        await session.Rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "daml", version = 2, text = ReopenedText } });
        (await WaitForAsync(session.Published, before, count => count == 0)).ShouldBeTrue("errors for the discarded text must clear");
        var hoverTask = session.Rpc.InvokeWithParameterObjectAsync<JObject>("textDocument/hover", new { textDocument = new { uri }, position = new { line = 3, character = 1 } });
        (await Task.WhenAny(hoverTask, Task.Delay(TimeSpan.FromSeconds(60)))).ShouldBe(hoverTask);
        var hover = (await hoverTask).ShouldNotBeNull();
        hover["contents"].ShouldNotBeNull();
        if (hover["range"] is { } range)
        {
            var lines = ReopenedText.Split('\n');
            foreach (var end in new[] { "start", "end" })
            {
                var line = (int)range[end]!["line"]!;
                line.ShouldBeInRange(0, lines.Length - 1);
                ((int)range[end]!["character"]!).ShouldBeInRange(0, lines[line].Length);
            }
        }
    }

    // multi-ide's coordinator sends an empty, unversioned list for every known file whenever
    // another file opens, and the package's server never sends the error again.
    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("multi-ide", true)]
    [InlineData("multi-ide", false)]
    [InlineData("ide", true)]
    [InlineData("ide", false)]
    public async Task Opening_another_file_leaves_the_error_in_place(string mode, bool closeFirst)
    {
        var package = Repository.Sample("broken");
        string UriOf(string name) => new Uri(Path.Combine(package, "daml", name)).AbsoluteUri;
        var typeError = UriOf("TypeError.daml");
        await using var session = await StartAsync(mode, package, typeError);
        var rpc = session.Rpc;
        var published = session.Published;
        object Opened(string name, int version) => new { textDocument = new { uri = UriOf(name), languageId = "daml", version, text = File.ReadAllText(Path.Combine(package, "daml", name)) } };

        await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", Opened("TypeError.daml", 1));
        (await WaitForAsync(published, 0, count => count > 0)).ShouldBeTrue("no diagnostics after the first open");
        if (closeFirst)
        {
            await rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri = typeError } });
        }
        await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", Opened("UnknownImport.daml", 1));
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (closeFirst)
        {
            await rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri = UriOf("UnknownImport.daml") } });
            await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", Opened("TypeError.daml", 2));
        }
        await Task.Delay(TimeSpan.FromSeconds(3));

        var all = Snapshot(published);
        _output.WriteLine($"{mode}, close first {closeFirst}: TypeError diagnostics counts in order: {string.Join(", ", all)}");
        all[^1].ShouldBeGreaterThan(0, "the last word on TypeError.daml cleared its error");
    }

    // damlc through the adapter, the way Visual Studio talks to it, counting the diagnostics
    // published for one file.
    private static async Task<Session> StartAsync(string mode, string package, string trackedUri)
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var process = new ProcessRunner().Start(new ProcessSpec(dpm, ["damlc", mode]) { WorkingDirectory = package });
        var stderr = process.StandardError.CopyToAsync(Stream.Null);
        var settings = new LspAdapterSettings();
        var tracker = new InitializeTracker();
        var documents = new OpenDocuments();
        var published = new List<int>();
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            new ClientToServerStream(process.StandardInput, settings, tracker, NullDamlLogger.Instance, documents),
            new ServerToClientStream(process.StandardOutput, settings, tracker, NullDamlLogger.Instance, documents),
            new JsonMessageFormatter()));
        rpc.AddLocalRpcMethod("textDocument/publishDiagnostics", new Action<JToken>(p =>
        {
            if (string.Equals(Uri.UnescapeDataString((string?)p["uri"] ?? ""), Uri.UnescapeDataString(trackedUri), StringComparison.OrdinalIgnoreCase))
            {
                lock (published)
                {
                    published.Add(((JArray?)p["diagnostics"])?.Count ?? 0);
                }
            }
        }));
        rpc.AddLocalRpcMethod("window/workDoneProgress/create", new Func<JToken, object?>(_ => null));
        rpc.AddLocalRpcMethod("client/registerCapability", new Func<JToken, object?>(_ => null));
        rpc.StartListening();
        var initialize = rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, rootUri = new Uri(package).AbsoluteUri, capabilities = new { } });
        (await Task.WhenAny(initialize, Task.Delay(TimeSpan.FromMinutes(2)))).ShouldBe(initialize, "damlc didn't answer initialize");
        await rpc.NotifyWithParameterObjectAsync("initialized", new { });
        return new Session(process, rpc, published, stderr);
    }

    private sealed class Session(IRunningProcess process, JsonRpc rpc, List<int> published, Task stderr) : IAsyncDisposable
    {
        public JsonRpc Rpc { get; } = rpc;

        public List<int> Published { get; } = published;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Rpc.NotifyAsync("exit");
            }
            catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException)
            {
            }
            Rpc.Dispose();
            // The stderr copy isn't tied to any synchronization context; waiting on it can't deadlock.
#pragma warning disable VSTHRD003
            await Task.WhenAny(stderr, Task.Delay(TimeSpan.FromSeconds(5)));
#pragma warning restore VSTHRD003
            process.Dispose();
        }
    }

    private static List<int> Snapshot(List<int> published)
    {
        lock (published)
        {
            return published.ToList();
        }
    }

    private static async Task<bool> WaitForAsync(List<int> published, int skip, Func<int, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (Snapshot(published).Skip(skip).Any(condition))
            {
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }
}
