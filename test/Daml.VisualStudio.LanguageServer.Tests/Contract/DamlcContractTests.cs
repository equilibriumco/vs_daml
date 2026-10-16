using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Lenses;
using Daml.VisualStudio.LanguageServer.Messages;
using Daml.VisualStudio.LanguageServer.Protocol;
using Daml.VisualStudio.LanguageServer.VirtualResources;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Daml.VisualStudio.LanguageServer.Tests.Contract;

// What the extension relies on from the real damlc, on every SDK and server mode we support.
// A failure here means damlc changed under us, not that our code did.
public class DamlcContractTests
{
    private readonly ITestOutputHelper _output;

    public DamlcContractTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Matrix() =>
        from sdk in new[] { "3.6.0", "3.5.2" }
        from mode in new[] { "multi-ide", "ide" }
        select new object[] { sdk, mode };

    private static ServerMode Mode(string mode) => mode == "ide" ? ServerMode.Ide : ServerMode.MultiIde;

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task Capabilities_are_the_ones_the_extension_was_built_for(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        await using var damlc = await RealDamlcSession.StartAsync(sample, main, Mode(mode));

        // Features the extension takes from the server, and ones it was decided to live without.
        foreach (var provided in new[] { "hoverProvider", "definitionProvider", "documentSymbolProvider", "codeActionProvider", "completionProvider", "codeLensProvider" })
        {
            IsOn(damlc.Capabilities[provided]).ShouldBeTrue($"{provided} is gone");
        }
        foreach (var missing in new[] { "referencesProvider", "renameProvider", "documentFormattingProvider", "documentHighlightProvider", "workspaceSymbolProvider", "signatureHelpProvider" })
        {
            IsOn(damlc.Capabilities[missing]).ShouldBeFalse($"{missing} appeared; the extension may want to use it now");
        }

        var snapshot = JObject.Parse(File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "lsp-capabilities", $"{mode}-{sdk}.json")));
        if (!JToken.DeepEquals(snapshot, damlc.Capabilities))
        {
            _output.WriteLine("Expected:");
            _output.WriteLine(snapshot.ToString());
            _output.WriteLine("Actual:");
            _output.WriteLine(damlc.Capabilities.ToString());
        }
        JToken.DeepEquals(snapshot, damlc.Capabilities).ShouldBeTrue($"damlc {mode} {sdk} announces different capabilities than the recorded snapshot; review the difference in the test output, then update test/fixtures/lsp-capabilities");
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task A_type_error_is_published_for_the_opened_file(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("broken", sdk);
        var file = sample.File(@"daml\TypeError.daml");
        await using var damlc = await RealDamlcSession.StartAsync(sample, file, Mode(mode));

        await damlc.OpenAsync(file);
        var diagnostics = await damlc.WaitForDiagnosticsAsync(file, d => d.Count > 0);

        diagnostics.ShouldNotBeNull("no diagnostics for TypeError.daml");
        var error = diagnostics.FirstOrDefault(d => (int?)d["severity"] == 1).ShouldNotBeNull("no error among the diagnostics");
        _output.WriteLine(error.ToString());
        ((int)error["range"]!["start"]!["line"]!).ShouldBe(Line(file, "\"forty-two\""));

        // The error names the file by its full path; the transcript must not.
        var transcript = string.Join("\n", damlc.Transcript);
        transcript.ShouldContain("${ROOT}/daml/TypeError.daml");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var forward = home.Replace('\\', '/');
        foreach (var spelling in new[] { home, forward, forward.Replace(":", "%3A") })
        {
            // JSON doubles backslashes, so look for that form too.
            transcript.IndexOf(spelling.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase).ShouldBe(-1, spelling);
            transcript.IndexOf(spelling, StringComparison.OrdinalIgnoreCase).ShouldBe(-1, spelling);
        }
        transcript.ShouldNotContain("total = \"forty-two\"", Case.Sensitive, "document text stays out of the transcript");
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task Hover_on_a_template_name_describes_it(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        await using var damlc = await OpenedAsync(sample, main, mode);

        var hover = await EventuallyAsync(() => damlc.RequestAsync("textDocument/hover", At(main, "template Asset", "Asset")), h => h is JObject);

        var text = hover!["contents"]!.ToString();
        _output.WriteLine(text);
        text.ShouldContain("Asset");
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task Definition_of_a_template_use_points_at_its_declaration(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        await using var damlc = await OpenedAsync(sample, main, mode);

        var result = await EventuallyAsync(() => damlc.RequestAsync("textDocument/definition", At(main, "createCmd Asset", "Asset")), IsLocation);

        var location = result is JArray array ? array[0] : result!;
        Unescaped((string)location["uri"]!).ShouldBe(Unescaped(RealDamlcSession.Uri(main)), StringCompareShould.IgnoreCase);
        ((int)location["range"]!["start"]!["line"]!).ShouldBe(Line(main, "template Asset"));
    }

    // The importing package only knows the name through the built DAR; the multi-package server
    // still lands in the other package's source when multi-package.yaml is in its folder. On the
    // import line itself damlc finds nothing, so the probe is a use of the name.
    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("3.6.0")]
    [InlineData("3.5.2")]
    public async Task Definition_of_a_name_from_another_package_opens_its_source(string sdk)
    {
        using var sample = SampleCopy.Create("multi-package", sdk);
        var build = await new ProcessRunner().RunAsync(
            new ProcessSpec(RequiredTools.Require(ExternalTool.Dpm), ["build", "--all"]) { WorkingDirectory = sample.Root },
            CancellationToken.None);
        build.ExitCode.ShouldBe(0, build.Stdout + build.Stderr);
        var main = sample.File(@"main\daml\Main.daml");
        var asset = sample.File(@"interfaces\daml\Asset.daml");
        await using var damlc = await OpenedAsync(sample, main, "multi-ide");

        var result = await EventuallyAsync(() => damlc.RequestAsync("textDocument/definition", At(main, "view = Asset.View", "View")), IsLocation);

        var location = result is JArray array ? array[0] : result!;
        _output.WriteLine(result!.ToString());
        Unescaped((string)location["uri"]!).ShouldBe(Unescaped(RealDamlcSession.Uri(asset)), StringCompareShould.IgnoreCase);
    }

    // Library definitions point into the package's .daml\package-database, which damlc sets up
    // when it starts; a copy without .daml has none until then.
    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task Definition_of_a_library_name_points_at_a_file_that_exists(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        Directory.Exists(sample.File(".daml")).ShouldBeFalse("the copy starts without build output");
        await using var damlc = await OpenedAsync(sample, main, mode);

        var result = await EventuallyAsync(() => damlc.RequestAsync("textDocument/definition", At(main, "PartyIdHint \"Alice\"", "PartyIdHint")), IsLocation);

        var location = result is JArray array ? array[0] : result!;
        var path = new Uri((string)location["uri"]!).LocalPath;
        _output.WriteLine(path);
        File.Exists(path.Length < LongPathRedirector.MaxPath ? path : @"\\?\" + path).ShouldBeTrue($"damlc points at {path}, which isn't there");

        // What Visual Studio gets: a path short enough to open.
        var copies = sample.File("library-copies");
        var redirected = new LongPathRedirector(copies, new LibraryCopier()).Redirect(result);
        var opened = new Uri((string)(redirected is JArray list ? list[0] : redirected!)["uri"]!).LocalPath;
        _output.WriteLine(opened);
        try
        {
            opened.Length.ShouldBeLessThan(LongPathRedirector.MaxPath);
            File.ReadAllText(opened).ShouldContain("PartyIdHint");
        }
        finally
        {
            // The copy is read-only, which would keep the sample folder from being deleted.
            if (File.Exists(opened))
            {
                File.SetAttributes(opened, FileAttributes.Normal);
            }
        }
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task Completion_offers_names_in_scope(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        await using var damlc = await OpenedAsync(sample, main, mode);
        var position = At(main, "createCmd Asset", "createCmd", offset: 3);

        var result = await EventuallyAsync(() => damlc.RequestAsync("textDocument/completion", position), r => Items(r).Any());

        Items(result).Select(i => (string?)i["label"]).ShouldContain("createCmd");
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task Document_symbols_list_the_template_and_the_script(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        await using var damlc = await OpenedAsync(sample, main, mode);

        var result = await EventuallyAsync(
            () => damlc.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri = RealDamlcSession.Uri(main) } }),
            r => r is JArray { Count: > 0 });

        var names = Flatten((JArray)result!).Select(s => (string?)s["name"]).ToList();
        _output.WriteLine(string.Join(", ", names));
        names.ShouldContain("Asset");
        names.ShouldContain("setup");
    }

    // Visual Studio never asks for code lenses, so the extension does; the answer must still be
    // the "Script results" lens shape the extension reads.
    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task The_script_gets_a_results_lens(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        await using var damlc = await OpenedAsync(sample, main, mode);

        var result = await EventuallyAsync(
            () => damlc.RequestAsync("textDocument/codeLens", new { textDocument = new { uri = RealDamlcSession.Uri(main) } }),
            r => ScriptLensParser.Parse(r).Count > 0);

        _output.WriteLine(result?.ToString() ?? "null");
        var lens = ScriptLensParser.Parse(result).ShouldHaveSingleItem();
        lens.Title.ShouldBe("Script: setup");
        lens.Resource.TopLevelDecl.ShouldBe("setup");
        lens.Resource.File.ShouldBe(main, StringCompareShould.IgnoreCase);
        File.ReadAllLines(main)[lens.Range.StartLine].ShouldStartWith("setup");
    }

    // From the lens to the window: the manager opens the lens's resource on the session, damlc
    // runs the script and sends its results, the extension's message target hands them back to
    // the manager, and the window gets the page with the placeholders the results host fills in.
    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task Opening_a_lens_resource_brings_the_script_results(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        await using var damlc = await OpenedAsync(sample, main, mode);
        var lenses = await EventuallyAsync(
            () => damlc.RequestAsync("textDocument/codeLens", new { textDocument = new { uri = RealDamlcSession.Uri(main) } }),
            r => ScriptLensParser.Parse(r).Count > 0);
        var lens = ScriptLensParser.Parse(lenses).Single();
        var channel = new DamlRpcChannel();
        channel.Attach(damlc.Rpc, sessionId: 1);
        var windows = new CapturingWindows();
        using var manager = new VirtualResourceManager(channel, windows, new RecordingLogger());
        damlc.VirtualResources.Target = manager;

        await manager.ShowAsync(lens.Title, lens.Resource.ToString(), CancellationToken.None);

        var deadline = DateTime.UtcNow + RealDamlcSession.Patience;
        while (windows.Pages.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }
        await manager.IdleAsync();
        windows.Shown.ShouldBe([lens.Title]);
        var page = windows.Pages.ShouldHaveSingleItem();
        page.ShouldContain("$webviewSrc");
        page.ShouldContain("$webviewCss");
        page.ShouldContain("Asset", Case.Sensitive, "the setup script creates an Asset");
    }

    private sealed class CapturingWindows : IVirtualResourceWindows
    {
        public ConcurrentQueue<string> Pages { get; } = new();

        public List<string> Shown { get; } = [];

        public event EventHandler<string>? Closed;

        public Task ShowAsync(string resource, string title, CancellationToken cancellationToken)
        {
            Shown.Add(title);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(string resource, string html)
        {
            // Progress messages come before the results on a slow machine; only pages count.
            if (html.Contains("$webviewSrc"))
            {
                Pages.Enqueue(html);
            }
            return Task.CompletedTask;
        }

        public Task AddNoteAsync(string resource, string text) => Task.CompletedTask;

        public void Close(string resource) => Closed?.Invoke(this, resource);
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [MemberData(nameof(Matrix))]
    public async Task Keep_alive_is_answered(string sdk, string mode)
    {
        using var sample = SampleCopy.Create("single-package", sdk);
        var main = sample.File(@"daml\Main.daml");
        await using var damlc = await RealDamlcSession.StartAsync(sample, main, Mode(mode));

        for (var i = 0; i < 2; i++)
        {
            var ping = damlc.Rpc.KeepAliveAsync(CancellationToken.None);
            (await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(ping, $"damlc {mode} {sdk} didn't answer daml/keepAlive");
            await ping;
        }
    }

    // ide sends no diagnostics at all for a clean file, so there's nothing to wait for; the
    // requests themselves are retried until damlc has checked the file.
    private static async Task<RealDamlcSession> OpenedAsync(SampleCopy sample, string file, string mode, [CallerMemberName] string scenario = "")
    {
        var damlc = await RealDamlcSession.StartAsync(sample, file, Mode(mode), scenario);
        await damlc.OpenAsync(file);
        return damlc;
    }

    // damlc may answer null while it is still busy with the file.
    private static async Task<JToken?> EventuallyAsync(Func<Task<JToken?>> request, Func<JToken?, bool> accept)
    {
        var deadline = DateTime.UtcNow + RealDamlcSession.Patience;
        JToken? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await request();
            if (accept(last))
            {
                return last;
            }
            await Task.Delay(500);
        }
        throw new ShouldAssertException($"No usable answer in time; the last one was: {last?.ToString() ?? "null"}");
    }

    private static object At(string path, string lineContains, string word, int offset = 0)
    {
        var lines = File.ReadAllLines(path);
        var line = Array.FindIndex(lines, l => l.Contains(lineContains));
        line.ShouldBeGreaterThanOrEqualTo(0, $"'{lineContains}' isn't in {path}");
        var character = lines[line].IndexOf(word, lines[line].IndexOf(lineContains, StringComparison.Ordinal), StringComparison.Ordinal) + offset;
        return new { textDocument = new { uri = RealDamlcSession.Uri(path) }, position = new { line, character } };
    }

    private static int Line(string path, string contains) => Array.FindIndex(File.ReadAllLines(path), l => l.Contains(contains));

    private static bool IsOn(JToken? capability) =>
        capability is not null && capability.Type != JTokenType.Null && !(capability.Type == JTokenType.Boolean && !(bool)capability);

    private static bool IsLocation(JToken? result) =>
        result is JObject { } single && single["uri"] is not null
        || result is JArray { Count: > 0 } many && many[0]["uri"] is not null;

    private static IEnumerable<JToken> Items(JToken? completion) =>
        completion switch
        {
            JArray items => items,
            JObject list when list["items"] is JArray items => items,
            _ => Enumerable.Empty<JToken>(),
        };

    private static IEnumerable<JToken> Flatten(JArray symbols) =>
        symbols.SelectMany(s => new[] { s }.Concat(s["children"] is JArray children ? Flatten(children) : Enumerable.Empty<JToken>()));

    private static string Unescaped(string uri) => Uri.UnescapeDataString(uri);
}
