using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

// Between the C# an app compiles and the Daml it was generated from, both ways, in the app
// template's solution after its first build.
public class CSharpBridgeTests
{
    private const int GoToDamlDefinition = 0x0206;
    private const string LensLayer = "DamlLensRows";
    private const string ReferencesRow = "Show the C# references of template Asset";
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    // The index of the generated C# is built in the background once the solution loads, so the
    // command is tried until it takes.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 15 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task Go_to_Daml_Definition_on_a_generated_type_opens_its_template()
    {
        var folder = await BuiltSolutionAsync();
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = Ide.OpenFile(Path.Combine(folder, @"Shop\Program.cs"));
            var text = view.TextBuffer.CurrentSnapshot.GetText();
            view.Caret.MoveTo(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, text.IndexOf("new Asset(", StringComparison.Ordinal) + "new As".Length));
            var main = Path.Combine(folder, @"Shop.Contracts\daml\Main.daml");

            var deadline = DateTime.UtcNow + Patience;
            var (path, line) = ("", "");
            while (DateTime.UtcNow < deadline)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Ide.ExecuteDamlCommand(view, GoToDamlDefinition);
                await Task.Delay(1000);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                (path, line) = Ide.ActiveDocumentAndLine();
                if (string.Equals(path, main, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            Assert.True(string.Equals(path, main, StringComparison.OrdinalIgnoreCase), $"Go to Daml Definition didn't open {main}; the active document is '{path}'.{Environment.NewLine}{Ide.PaneText("Daml", 4000)}");
            Assert.StartsWith("template Asset", line.Trim(), StringComparison.Ordinal);
        }
        finally
        {
            await CloseAsync();
        }
    }

    // The row above the template counts its uses in the app and opens them in a window; once the
    // Daml changes without a build, the count gives way to saying the C# is out of date.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 15 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task The_CSharp_references_row_of_a_template_lists_its_uses_in_the_app()
    {
        var folder = await BuiltSolutionAsync();
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = Ide.OpenFile(Path.Combine(folder, @"Shop.Contracts\daml\Main.daml"));
            await Ide.WaitForServerAsync(ready: true, Patience);

            var counted = await WaitForRowTextAsync(view, t => Regex.IsMatch(t, @"^C#: [1-9]\d* references?$"));
            var count = int.Parse(Regex.Match(counted.Text, @"\d+").Value, System.Globalization.CultureInfo.InvariantCulture);
            counted.Link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            var caption = await Ide.WaitForToolWindowAsync(c => c.StartsWith("Daml: C# References — Main:Asset", StringComparison.Ordinal) && c.EndsWith($"({count})", StringComparison.Ordinal), Patience);
            Assert.True(caption is not null, $"No 'Daml: C# References — Main:Asset ({count})' window opened.{Environment.NewLine}{Ide.PaneText("Daml", 4000)}");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var rows = Ide.TableRows("DamlCSharpReferences", StandardTableKeyNames.DocumentName, StandardTableKeyNames.Text);
            Assert.Equal(count, rows.Count);
            Assert.Contains(rows, r => r[StandardTableKeyNames.DocumentName] is string document && document.EndsWith(@"\Shop\Program.cs", StringComparison.OrdinalIgnoreCase)
                && r[StandardTableKeyNames.Text] is string code && code.Contains("Asset"));

            using (var edit = view.TextBuffer.CreateEdit())
            {
                edit.Insert(view.TextBuffer.CurrentSnapshot.Length, Environment.NewLine + "-- edited" + Environment.NewLine);
                edit.Apply();
            }
            Ide.Dte().ActiveDocument.Save();
            await WaitForRowTextAsync(view, t => t == "C#: out of date (build to update)");
        }
        finally
        {
            await CloseAsync();
        }
    }

    private static async Task<string> BuiltSolutionAsync()
    {
        var folder = await Ide.NewSolutionFromTemplateAsync("daml-dotnet-app", "Shop");
        await Ide.OpenSolutionAsync(Path.Combine(folder, "Shop.slnx"));
        var failed = await Ide.BuildSolutionAsync(BuildTimeout);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Assert.True(failed == 0, "The build failed." + Environment.NewLine + Ide.BuildReport());
        return folder;
    }

    private static async Task CloseAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Ide.CloseAllDocuments();
        await Ide.CloseSolutionAsync();
    }

    // The template's C# references segment once its text is what the test waits for.
    private static async Task<(Hyperlink Link, string Text)> WaitForRowTextAsync(IWpfTextView view, Func<string, bool> accept)
    {
        var deadline = DateTime.UtcNow + Patience;
        var seen = "no row";
        while (DateTime.UtcNow < deadline)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var link = view.GetAdornmentLayer(LensLayer).Elements
                .Select(e => e.Adornment).OfType<TextBlock>()
                .SelectMany(row => row.Inlines.OfType<Hyperlink>())
                .FirstOrDefault(h => AutomationProperties.GetName(h) == ReferencesRow);
            if (link is not null)
            {
                seen = string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text));
                if (accept(seen))
                {
                    return (link, seen);
                }
            }
            await Task.Delay(500);
        }
        Assert.Fail($"The C# references row of template Asset never showed the expected text; last seen: '{seen}'.{Environment.NewLine}{Ide.PaneText("Daml", 4000)}");
        return default;
    }
}
