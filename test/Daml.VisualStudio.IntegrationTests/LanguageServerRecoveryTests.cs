using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

// The server losing its process, a workspace that only gets its daml.yaml later, another SDK and a
// file from elsewhere: Visual Studio has to keep serving the folder through all of them.
public class LanguageServerRecoveryTests
{
    private static readonly TimeSpan ServerStart = TimeSpan.FromMinutes(2);

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task A_killed_server_comes_back_by_itself_with_its_diagnostics()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var folder = Ide.CopySample("broken");
        Ide.OpenFolder(folder);
        try
        {
            Ide.OpenFile(Path.Combine(folder, "daml", "TypeError.daml"));
            await Ide.WaitForServerAsync(ready: true, ServerStart);

            Assert.True(Ide.KillLanguageServersOfThisInstance() > 0, "No damlc process of this Visual Studio was running.");

            await Ide.WaitForServerAsync(ready: false, TimeSpan.FromSeconds(30));
            await Ide.WaitForServerAsync(ready: true, ServerStart);
            Assert.True(await ErrorListHasAsync("TypeError.daml", TimeSpan.FromSeconds(60)), "The error for TypeError.daml didn't come back after the restart.");
        }
        finally
        {
            await Ide.CloseFolderAsync();
        }
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 5 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task A_daml_yaml_created_later_starts_the_server()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var folder = Ide.CopySample("single-package");
        var manifest = Path.Combine(folder, "daml.yaml");
        var content = File.ReadAllText(manifest);
        File.Delete(manifest);
        Ide.OpenFolder(folder);
        try
        {
            Ide.OpenFile(Path.Combine(folder, "daml", "Main.daml"));
            await Task.Delay(TimeSpan.FromSeconds(5));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await Ide.WaitForServerAsync(ready: false, TimeSpan.Zero);

            File.WriteAllText(manifest, content);

            await Ide.WaitForServerAsync(ready: true, ServerStart);
        }
        finally
        {
            await Ide.CloseFolderAsync();
        }
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 5 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task A_package_on_sdk_3_5_is_served_and_a_file_from_elsewhere_does_not_disturb_it()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var folder = Ide.CopySample("broken", sdkVersion: "3.5.2");
        Ide.OpenFolder(folder);
        try
        {
            Ide.OpenFile(Path.Combine(folder, "daml", "TypeError.daml"));
            await Ide.WaitForServerAsync(ready: true, ServerStart);
            Assert.True(await ErrorListHasAsync("TypeError.daml", TimeSpan.FromSeconds(60)), "No error for TypeError.daml on SDK 3.5.2.");

            // A Daml file with no daml.yaml above it once took multi-ide down entirely.
            Ide.OpenFile(Path.Combine(Ide.RepoRoot(), "test", "fixtures", "lexer", "keywords.daml"));
            var watch = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < watch)
            {
                await Ide.WaitForServerAsync(ready: true, TimeSpan.Zero);
                await Task.Delay(250);
            }
        }
        finally
        {
            await Ide.CloseFolderAsync();
        }
    }

    private static async Task<bool> ErrorListHasAsync(string fileName, TimeSpan timeout)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var errorList = ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList)) as IErrorList ?? throw new InvalidOperationException("The Error List is not available.");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await errorList.TableControl.ForceUpdateAsync();
            if (errorList.TableControl.Entries.Any(e =>
                e.TryGetValue(StandardTableKeyNames.DocumentName, out var document)
                && document is string path
                && path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }
            await Task.Delay(500);
        }
    }
}
