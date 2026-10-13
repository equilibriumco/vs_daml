using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;
using Microsoft.VisualStudio.Workspace.Indexing;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class BuildTests
{
    // Building calls the real dpm, which can be slow on a cold SDK.
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(4);

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Building_the_broken_sample_puts_daml_build_errors_in_the_error_list()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        // A copy, so dpm's .daml output doesn't land in the repository.
        var folder = Ide.CopySample("broken");
        Ide.OpenFolder(folder);
        try
        {
            await BuildAsync(folder);
        }
        finally
        {
            await Ide.CloseFolderAsync();
        }
    }

    private static async Task BuildAsync(string folder)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Ide.Dte();
        // Build commands appear once Open Folder has indexed the manifests.
        await WaitForIndexAsync(TimeSpan.FromMinutes(1));
        // Open Folder offers Build for the active document when the index holds a build context
        // for it, and Build All once it knows of anything buildable; which of the two carries it
        // isn't the same as with a solution, so the manifest is opened and the first offered is taken.
        Ide.OpenFile(Path.Combine(folder, "daml.yaml"));
        string[] candidates = ["Build.BuildSelection", "Build.BuildSolution", "Build.BuildAll"];
        string? available = null;
        var ready = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (available is null && DateTime.UtcNow < ready)
        {
            available = candidates.FirstOrDefault(name => IsAvailable(dte, name));
            if (available is null)
            {
                await Task.Delay(500);
            }
        }
        if (available is null)
        {
            Assert.Fail($"No build command became available for {folder} (open: {dte.Solution?.FullName}): {string.Join(", ", candidates)}. "
                + $"Available Build.* commands: {AvailableBuildCommands(dte)}. {await OpenFolderStateAsync(folder)}");
        }
        dte.ExecuteCommand(available);

        var errorList = ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList)) as IErrorList ?? throw new InvalidOperationException("The Error List is not available.");
        var deadline = DateTime.UtcNow + BuildTimeout;
        var count = 0;
        while (DateTime.UtcNow < deadline)
        {
            await errorList.TableControl.ForceUpdateAsync();
            // Build errors reach the Error List through Visual Studio's build messages, under our codes.
            count = errorList.TableControl.Entries.Count(e => e.TryGetValue(StandardTableKeyNames.ErrorCode, out var code) && Equals(code, "DAML0001"));
            if (count > 0)
            {
                break;
            }
            await Task.Delay(1000);
        }

        Assert.True(count >= 1, "No DAML0001 build errors reached the Error List.");
    }

    private static async Task WaitForIndexAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var workspace = Ide.ComponentModel().GetService<IVsFolderWorkspaceService>()?.CurrentWorkspace;
            var index = workspace is null ? null : await workspace.GetIndexWorkspaceServiceAsync();
            if (index?.State == IndexWorkspaceState.Completed)
            {
                return;
            }
            await Task.Delay(250);
        }
    }

    // What Open Folder knows about the folder: how far indexing got, and the build contexts it
    // has for daml.yaml. Without those, Visual Studio has nothing to build.
    private static async Task<string> OpenFolderStateAsync(string folder)
    {
        var workspace = Ide.ComponentModel().GetService<IVsFolderWorkspaceService>()?.CurrentWorkspace;
        if (workspace is null)
        {
            return "Open Folder has no current workspace.";
        }
        var index = await workspace.GetIndexWorkspaceServiceAsync();
        var contexts = await workspace.GetFileContextsAsync(Path.Combine(folder, "daml.yaml"), [BuildContextTypes.BuildContextTypeGuid, BuildContextTypes.CleanContextTypeGuid], CancellationToken.None);
        return $"Workspace {workspace.Location}, index {index?.State}; build contexts for daml.yaml: "
            + (contexts.Count == 0 ? "none" : string.Join(", ", contexts.SelectMany(g => g).Select(c => $"{c.DisplayName} ({c.ProviderType})")));
    }

    // Every Build.* command Visual Studio would run right now: when none of the expected ones is
    // offered, this says which are, which is the next thing to know.
    private static string AvailableBuildCommands(DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var names = new List<string>();
        foreach (Command command in dte.Commands)
        {
            if (command.Name is { } name && name.StartsWith("Build.", StringComparison.Ordinal) && IsAvailable(dte, name))
            {
                names.Add(name);
            }
        }
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static bool IsAvailable(DTE dte, string command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return dte.Commands.Item(command).IsAvailable;
        }
        catch (ArgumentException)
        {
            // Not a command this Visual Studio knows.
            return false;
        }
    }
}
