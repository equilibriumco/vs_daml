using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;
using Microsoft.VisualStudio.Workspace.Indexing;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

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

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task The_manifest_build_context_runs_without_an_active_document_and_reports_exact_locations()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var folder = Ide.CopySample("broken");
        Ide.OpenFolder(folder);
        try
        {
            Ide.CloseAllDocuments();
            await WaitForIndexAsync(TimeSpan.FromMinutes(1));
            var workspace = Ide.ComponentModel().GetService<IVsFolderWorkspaceService>()!.CurrentWorkspace;
            Assert.NotNull(workspace);
            var contexts = await workspace!.GetFileContextsAsync(Path.Combine(folder, "daml.yaml"), [BuildContextTypes.BuildContextTypeGuid], CancellationToken.None);
            var context = Assert.Single(contexts.SelectMany(group => group), item => item.ProviderType == new Guid("202da85d-ff38-4820-b89e-d9f8db6d85b3"));
            var build = Assert.IsAssignableFrom<IBuildFileContext>(context.Context);
            var progress = new RecordingProgress();
            using var timeout = new CancellationTokenSource(BuildTimeout);
            Assert.False(await build.ExecuteBuildAsync(progress, timeout.Token));
            Assert.Contains(progress.Messages, message => message.Code == "EQD0001"
                && string.Equals(message.File, Path.Combine(folder, "daml", "TypeError.daml"), StringComparison.OrdinalIgnoreCase)
                && message.LineNumber == 5 && message.ColumnNumber == 9);
        }
        finally { await Ide.CloseFolderAsync(); }
    }

    private sealed class RecordingProgress : IBuildActionProgress
    {
        public List<BuildMessage> Messages { get; } = new();
        public Task ReportAsync(BuildMessage buildMessage, object? context)
        {
            Messages.Add(buildMessage);
            return Task.CompletedTask;
        }
        public void Report(IFileContextActionProgressUpdate value) => throw new NotSupportedException();
    }

    private static async Task BuildAsync(string folder)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Ide.Dte();
        // Build commands appear once Open Folder has indexed the manifests.
        await WaitForIndexAsync(TimeSpan.FromMinutes(1));
        // This test covers the active-manifest Build Selection command; the file-context test
        // separately verifies the folder action without an active document.
        Ide.OpenFile(Path.Combine(folder, "daml.yaml"));
        const string BuildSelectionCommand = "Build.BuildSelection";
        var ready = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (!IsAvailable(dte, BuildSelectionCommand) && DateTime.UtcNow < ready) { await Task.Delay(500); }
        Assert.True(IsAvailable(dte, BuildSelectionCommand), $"{BuildSelectionCommand} is unavailable for daml.yaml: {await OpenFolderStateAsync(folder)}");
        dte.ExecuteCommand(BuildSelectionCommand);

        var errorList = ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList)) as IErrorList ?? throw new InvalidOperationException("The Error List is not available.");
        var deadline = DateTime.UtcNow + BuildTimeout;
        var found = false;
        var expected = Path.Combine(folder, "daml", "TypeError.daml");
        while (DateTime.UtcNow < deadline)
        {
            await errorList.TableControl.ForceUpdateAsync();
            // Build errors reach the Error List through Visual Studio's build messages, under our codes.
            found = errorList.TableControl.Entries.Any(entry =>
                entry.TryGetValue(StandardTableKeyNames.ErrorCode, out var code) && Equals(code, "EQD0001")
                && entry.TryGetValue(StandardTableKeyNames.DocumentName, out var document) && document is string path
                && string.Equals(path, expected, StringComparison.OrdinalIgnoreCase)
                && entry.TryGetValue(StandardTableKeyNames.Line, out var line) && Equals(line, 4)
                && entry.TryGetValue(StandardTableKeyNames.Column, out var column) && Equals(column, 8));
            if (found) { break; }
            await Task.Delay(1000);
        }

        Assert.True(found, $"No EQD0001 build error for {expected}:5:9 reached the Error List. {Ide.PaneText("Daml Build", 12000)}");
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
        Assert.Fail("Open Folder indexing did not finish within the build test timeout.");
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
