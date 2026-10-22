using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

// The scale fixture of the test strategy (20 Daml packages, 1000 files, 100k lines, 500 templates,
// 10 C# projects) in Visual Studio, after its first build: how long the first diagnostic takes,
// the C# references rows of templates, and background sync after a save. The times go to
// artifacts\scale\ide.json; the targets are the test strategy's.
public class ScaleTests
{
    private const string LensLayer = "DamlLensRows";
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstDiagnosticTarget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LensTarget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SyncTarget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private readonly ITestOutputHelper _output;

    public ScaleTests(ITestOutputHelper output) => _output = output;

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 30 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task The_scale_fixture_answers_within_the_test_strategys_targets()
    {
        var fixture = await Ide.NewScaleFixtureAsync();
        File.WriteAllText(Path.Combine(fixture, "Directory.Build.props"), """
            <Project>
              <PropertyGroup Condition="'$(MSBuildProjectName)' == 'App01'">
                <BaseIntermediateOutputPath>intermediate\</BaseIntermediateOutputPath>
              </PropertyGroup>
            </Project>
            """);
        await Ide.OpenSolutionAsync(Path.Combine(fixture, "Scale.slnx"));
        object? syncBefore = null;
        try
        {
            var built = Stopwatch.StartNew();
            var failed = await Ide.BuildSolutionAsync(BuildTimeout);
            built.Stop();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Assert.True(failed == 0, "The build failed." + Environment.NewLine + Ide.BuildReport());

            // The first Daml file opened starts the language server; the next ones find it running.
            var diagnostics = new List<double>();
            foreach (var package in new[] { 5, 9, 13, 17, 20, 2 })
            {
                diagnostics.Add((await FirstDiagnosticAsync(Module(fixture, package, 30))).TotalSeconds);
            }
            Ide.CloseAllDocuments();

            // Counted only once on screen; the first one waits for the index of the solution's C#.
            var lenses = new List<double>();
            for (var package = 1; package <= 20; package++)
            {
                lenses.Add((await ReferencesRowAsync(Module(fixture, package, 1))).TotalSeconds);
            }
            Ide.CloseAllDocuments();

            syncBefore = await Ide.SetDamlOptionAsync("LiveBindingSync", true);
            var sync = (await BackgroundSyncAsync(fixture)).TotalSeconds;

            var warmLenses = lenses.Skip(1).ToList();
            var result = new
            {
                Date = DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture),
                FirstBuildSeconds = Math.Round(built.Elapsed.TotalSeconds, 1),
                ColdDiagnosticSeconds = Math.Round(diagnostics[0], 2),
                WarmDiagnosticSeconds = diagnostics.Skip(1).Select(d => Math.Round(d, 2)).ToList(),
                LensSeconds = lenses.Select(l => Math.Round(l, 2)).ToList(),
                WarmLensP95Seconds = Math.Round(P95(warmLenses), 2),
                SyncSeconds = Math.Round(sync, 2),
            };
            var report = JsonConvert.SerializeObject(result, Formatting.Indented);
            _output.WriteLine(report);
            Directory.CreateDirectory(Path.Combine(Ide.RepoRoot(), @"artifacts\scale"));
            File.WriteAllText(Path.Combine(Ide.RepoRoot(), @"artifacts\scale\ide.json"), report);

            Assert.True(diagnostics.Max() <= FirstDiagnosticTarget.TotalSeconds, $"A first diagnostic took over {FirstDiagnosticTarget.TotalSeconds:0} s: {report}");
            Assert.True(P95(warmLenses) <= LensTarget.TotalSeconds, $"The C# references rows took over {LensTarget.TotalSeconds:0} s at p95: {report}");
            Assert.True(sync <= SyncTarget.TotalSeconds, $"Background sync took over {SyncTarget.TotalSeconds:0} s: {report}");
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (syncBefore is not null)
            {
                await Ide.SetDamlOptionAsync("LiveBindingSync", syncBefore);
            }
            Ide.CloseAllDocuments();
            await Ide.CloseSolutionAsync();
        }
    }

    private static string Module(string fixture, int package, int module) =>
        Path.Combine(fixture, $@"Pkg{package:00}\daml\Pkg{package:00}\M{module:00}.daml");

    private static double P95(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[(int)Math.Ceiling(0.95 * sorted.Count) - 1];
    }

    // From opening the file to the Error List showing the type error typed into it (never saved).
    private static async Task<TimeSpan> FirstDiagnosticAsync(string file)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var clock = Stopwatch.StartNew();
        var view = Ide.OpenFile(file);
        using (var edit = view.TextBuffer.CreateEdit())
        {
            edit.Insert(view.TextBuffer.CurrentSnapshot.Length, "\nbroken : Int\nbroken = \"text\"\n");
            edit.Apply();
        }
        while (clock.Elapsed < Patience)
        {
            if (await Ide.ErrorsInAsync(file) > 0)
            {
                return clock.Elapsed;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"No error for {file} reached the Error List within {Patience.TotalSeconds:0} s.{Environment.NewLine}{Ide.PaneText("EQ Daml Tools: Language Server", 6000)}");
        return clock.Elapsed;
    }

    // From opening the file to the row above its template counting the template's C# uses.
    private static async Task<TimeSpan> ReferencesRowAsync(string file)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var clock = Stopwatch.StartNew();
        var view = Ide.OpenFile(file);
        var seen = "no row";
        while (clock.Elapsed < Patience)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var link = view.GetAdornmentLayer(LensLayer).Elements
                .Select(e => e.Adornment).OfType<TextBlock>()
                .SelectMany(row => row.Inlines.OfType<Hyperlink>())
                .FirstOrDefault(h => AutomationProperties.GetName(h) == "Show the C# references of template Asset1");
            if (link is not null)
            {
                seen = string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text));
                if (seen.StartsWith("C#: ", StringComparison.Ordinal) && char.IsDigit(seen[4]))
                {
                    return clock.Elapsed;
                }
            }
            await Task.Delay(50);
        }
        Assert.Fail($"The C# references row of Asset1 in {file} never counted; last seen: '{seen}'.{Environment.NewLine}{Ide.PaneText("EQ Daml Tools", 6000)}");
        return clock.Elapsed;
    }

    // From saving a new template into a package to its C# in the generated code of a project that
    // uses the package, with nothing built by hand.
    private static async Task<TimeSpan> BackgroundSyncAsync(string fixture)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var view = Ide.OpenFile(Module(fixture, 1, 1));
        using (var edit = view.TextBuffer.CreateEdit())
        {
            edit.Insert(view.TextBuffer.CurrentSnapshot.Length, "\ntemplate Extra1\n  with\n    issuer : Party\n  where\n    signatory issuer\n");
            edit.Apply();
        }
        var generated = Path.Combine(fixture, @"App01\intermediate\Debug\net10.0\Daml");
        var clock = Stopwatch.StartNew();
        Ide.Dte().ActiveDocument.Save();
        while (clock.Elapsed < Patience)
        {
            await Task.Delay(250);
            if (Directory.Exists(generated) && Directory.EnumerateFiles(generated, "Extra1.cs", SearchOption.AllDirectories).Any())
            {
                return clock.Elapsed;
            }
        }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Assert.Fail($"Background sync didn't generate Extra1 into {generated} within {Patience.TotalSeconds:0} s.{Environment.NewLine}{Ide.PaneText("EQ Daml Tools", 6000)}");
        return clock.Elapsed;
    }
}
