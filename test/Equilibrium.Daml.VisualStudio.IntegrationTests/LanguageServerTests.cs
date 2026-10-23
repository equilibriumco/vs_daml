using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

// The extension in Visual Studio with the real damlc behind it: Open Folder on a sample, then what
// a user sees. Each test works on a copy of the sample and closes the folder afterwards.
public class LanguageServerTests
{
    private static readonly TimeSpan ServerStart = TimeSpan.FromMinutes(2);

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 5 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task A_type_error_reaches_the_error_list()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var folder = Ide.CopySample("broken");
        Ide.OpenFolder(folder);
        try
        {
            Ide.OpenFile(Path.Combine(folder, "daml", "TypeError.daml"));
            await Ide.WaitForServerAsync(ready: true, ServerStart);

            var errorList = ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList)) as IErrorList ?? throw new InvalidOperationException("The Error List is not available.");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            var found = false;
            while (!found && DateTime.UtcNow < deadline)
            {
                await errorList.TableControl.ForceUpdateAsync();
                found = errorList.TableControl.Entries.Any(e =>
                    e.TryGetValue(StandardTableKeyNames.DocumentName, out var document)
                    && document is string path
                    && path.EndsWith("TypeError.daml", StringComparison.OrdinalIgnoreCase));
                if (!found)
                {
                    await Task.Delay(500);
                }
            }

            Assert.True(found, "No error for TypeError.daml reached the Error List within 60 s of the server being ready.");
        }
        finally
        {
            await Ide.CloseFolderAsync();
        }
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 8 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Go_to_definition_on_a_name_from_another_package_opens_its_source()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var folder = Ide.CopySample("multi-package");
        // The importing package only knows the other one through its DAR.
        await Task.Run(() => Ide.DpmAsync(folder, "build", "--all"));
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Ide.OpenFolder(folder);
        try
        {
            var view = Ide.OpenFile(Path.Combine(folder, "main", "daml", "Main.daml"));
            await Ide.WaitForServerAsync(ready: true, ServerStart);
            var line = view.TextBuffer.CurrentSnapshot.Lines.First(l => l.GetText().Contains("Asset.View"));
            var target = Path.Combine(folder, "interfaces", "daml", "Asset.daml");

            // damlc answers nothing until it has checked the file, so ask again until it does.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            var opened = false;
            while (!opened && DateTime.UtcNow < deadline)
            {
                view.Caret.MoveTo(new SnapshotPoint(line.Snapshot, line.Start + line.GetText().IndexOf("View", StringComparison.Ordinal) + 1));
                Ide.GoToDefinition(view);
                await Task.Delay(2000);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                opened = string.Equals(Ide.Dte().ActiveDocument?.FullName, target, StringComparison.OrdinalIgnoreCase);
                if (!opened)
                {
                    Ide.OpenFile(Path.Combine(folder, "main", "daml", "Main.daml"));
                }
            }

            Assert.True(opened, $"Go To Definition on Asset.View didn't open {target}; the active document is {Ide.Dte().ActiveDocument?.FullName}.{Environment.NewLine}{Ide.PaneText("EQ Daml Tools: Language Server", 12000)}");
        }
        finally
        {
            await Ide.CloseFolderAsync();
        }
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Restart_from_the_daml_menu_brings_the_server_back()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var folder = Ide.CopySample("single-package");
        Ide.OpenFolder(folder);
        try
        {
            Ide.OpenFile(Path.Combine(folder, "daml", "Main.daml"));
            await Ide.WaitForServerAsync(ready: true, ServerStart);

            Ide.RunDamlCommand(Ide.RestartLanguageServerCommand);

            await Ide.WaitForServerAsync(ready: false, TimeSpan.FromSeconds(30));
            await Ide.WaitForServerAsync(ready: true, ServerStart);
        }
        finally
        {
            await Ide.CloseFolderAsync();
        }
    }
}
