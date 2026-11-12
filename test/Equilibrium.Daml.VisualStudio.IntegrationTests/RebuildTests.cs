using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

// Rebuild cleans a package's DAR before building it again. The multi-package server restarts the
// packages that use the DAR when it disappears, and they fail without it; once it is back they
// have to be serving again, without a restart of the server.
public class RebuildTests
{
    private const string ServerPane = "EQ Daml Tools: Language Server";
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(10);

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 15 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task After_Rebuild_Solution_a_package_using_a_rebuilt_DAR_has_no_errors_again()
    {
        var root = Ide.CopyFixtureFolder("solution-mode");
        var app = Path.Combine(root, "app");
        File.WriteAllText(Path.Combine(app, "multi-package.yaml"), "packages:\n  - ./Contracts\n  - ./Contracts.Tests\n  - ../shared/Lib\n");
        var test = Path.Combine(app, @"Contracts.Tests\daml\Test.daml");
        await Ide.OpenSolutionAsync(Path.Combine(app, "SolutionMode.slnx"));
        try
        {
            var failed = await Ide.BuildSolutionAsync(BuildTimeout);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Assert.True(failed == 0, "The build failed." + Environment.NewLine + Ide.BuildReport());
            Ide.OpenFile(test);
            await Ide.WaitForServerAsync(ready: true, Patience);
            Assert.True(await NoErrorsForAWhileAsync(test), $"{Path.GetFileName(test)} has errors before the rebuild.{Environment.NewLine}{Report()}");

            failed = await Ide.RebuildSolutionAsync(BuildTimeout);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Assert.True(failed == 0, "The rebuild failed." + Environment.NewLine + Ide.BuildReport());

            Assert.True(await NoErrorsForAWhileAsync(test), $"{Path.GetFileName(test)} still has errors after the rebuild.{Environment.NewLine}{Report()}");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Assert.Contains("Rebuilt DARs", Ide.PaneText(ServerPane, 20000), StringComparison.Ordinal);
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Ide.CloseAllDocuments();
            await Ide.CloseSolutionAsync();
        }
    }

    // The Error List can be empty for a moment while a package restarts, so no errors have to
    // last a while before they count.
    private static async Task<bool> NoErrorsForAWhileAsync(string file)
    {
        var deadline = DateTime.UtcNow + Patience;
        DateTime? clearSince = null;
        while (DateTime.UtcNow < deadline)
        {
            if (await Ide.ErrorsInAsync(file) == 0)
            {
                clearSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - clearSince >= Settled)
                {
                    return true;
                }
            }
            else
            {
                clearSince = null;
            }
            await Task.Delay(500);
        }
        return false;
    }

    private static string Report()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Ide.ErrorListText() + Environment.NewLine + "Language server pane (end):" + Environment.NewLine + Ide.PaneText(ServerPane, 12000);
    }
}
