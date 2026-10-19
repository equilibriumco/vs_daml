using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

// A solution of Daml projects, one of them outside the solution's folder, which the server serves
// all of; listed together in a multi-package.yaml, they reach each other's sources.
public class SolutionModeTests
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 15 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Go_To_Definition_crosses_the_packages_of_the_solution_once_they_are_listed_together()
    {
        var root = Ide.CopyFixtureFolder("solution-mode");
        var app = Path.Combine(root, "app");
        await Ide.OpenSolutionAsync(Path.Combine(app, "SolutionMode.slnx"));
        try
        {
            // The tests' package and the contracts know the other packages through their DARs.
            var failed = await Ide.BuildSolutionAsync(BuildTimeout);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Assert.True(failed == 0, "The build failed." + Environment.NewLine + Ide.BuildReport());
            File.WriteAllText(Path.Combine(app, "multi-package.yaml"), "packages:\n  - ./Contracts\n  - ./Contracts.Tests\n  - ../shared/Lib\n");

            var test = Path.Combine(app, @"Contracts.Tests\daml\Test.daml");
            var asset = Path.Combine(app, @"Contracts\daml\Asset.daml");
            await GoToDefinitionAsync(test, "createCmd Asset", "Asset", asset);
            await GoToDefinitionAsync(asset, "label : Label", "Label", Path.Combine(root, @"shared\Lib\daml\Lib.daml"));
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Ide.CloseAllDocuments();
            await Ide.CloseSolutionAsync();
        }
    }

    // damlc answers nothing until it has checked the file, and the server restarts for the new
    // multi-package.yaml first, so Go To Definition is asked again until it lands.
    private static async Task GoToDefinitionAsync(string file, string context, string word, string target)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var deadline = DateTime.UtcNow + Patience;
        var landed = "";
        while (DateTime.UtcNow < deadline)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = Ide.OpenFile(file);
            var line = view.TextBuffer.CurrentSnapshot.Lines.First(l => l.GetText().Contains(context));
            view.Caret.MoveTo(new SnapshotPoint(line.Snapshot, line.Start + line.GetText().IndexOf(context, StringComparison.Ordinal) + context.IndexOf(word, StringComparison.Ordinal) + 1));
            Ide.GoToDefinition(view);
            await Task.Delay(2000);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            landed = Ide.Dte().ActiveDocument?.FullName ?? "";
            if (string.Equals(landed, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        Assert.Fail($"Go To Definition on {word} in {Path.GetFileName(file)} didn't open {target}; the active document is '{landed}'.{Environment.NewLine}{Ide.PaneText("Daml Language Server", 12000)}");
    }
}
