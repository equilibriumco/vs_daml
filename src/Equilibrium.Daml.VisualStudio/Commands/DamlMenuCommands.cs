using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.Build.Core;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.VisualStudio.Build;
using Equilibrium.Daml.VisualStudio.Core.Build;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.LanguageClient;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Equilibrium.Daml.VisualStudio.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.Commands;

// Command ids of the Extensions > EQ Daml Tools menu; they must match DamlPackage.vsct.
internal static class DamlMenuIds
{
    public const string CommandSetString = BuildIds.CommandSetString;
    public const int RestartLanguageServer = 0x0200;
    public const int ShowLanguageServerLog = 0x0201;
    public const int BuildCurrentPackage = 0x0202;
    public const int TestCurrentPackage = 0x0203;
    public const int OpenDocumentation = 0x0204;
    public const int ShowScriptResultsAtCaret = 0x0205;

    // Not on the Daml menu: the C# code window's context menu, handled by the editor.
    public const int GoToDamlDefinition = 0x0206;

    public const int ExportDiagnostics = 0x0207;

    public const string DocumentationUrl = "https://docs.digitalasset.com/";
}

/// <summary>Restarts the language server, also after it crashed too often to restart by itself.</summary>
[Command(DamlMenuIds.CommandSetString, DamlMenuIds.RestartLanguageServer)]
internal sealed class RestartLanguageServerCommand : BaseCommand<RestartLanguageServerCommand>
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);
    private static int s_restarting;

    // The status bar keeps a message until something replaces it, so the outcome replaces this one.
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        if (Interlocked.Exchange(ref s_restarting, 1) != 0)
        {
            await VS.StatusBar.ShowMessageAsync("The Daml language server is already restarting…");
            return;
        }
        var client = await VS.GetMefServiceAsync<DamlLanguageClient>();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(object? sender, SessionReadyEventArgs args) => ready.TrySetResult(true);
        client.Host.SessionReady += OnReady;
        try
        {
            client.Host.Log(DamlLogLevel.Information, "Restart Language Server chosen from the Daml menu.");
            client.Host.Supervisor.ResetCircuit();
            await VS.StatusBar.ShowMessageAsync("Restarting the Daml language server…");
            if (await Task.WhenAny(ready.Task, Task.Delay(Patience)) == ready.Task)
            {
                await VS.StatusBar.ShowMessageAsync("EQ Daml: language server restarted.");
            }
            else
            {
                await VS.StatusBar.ClearAsync();
            }
        }
        finally
        {
            client.Host.SessionReady -= OnReady;
            Volatile.Write(ref s_restarting, 0);
        }
    }
}

/// <summary>Opens the Output window on the EQ Daml Tools: Language Server pane.</summary>
[Command(DamlMenuIds.CommandSetString, DamlMenuIds.ShowLanguageServerLog)]
internal sealed class ShowLanguageServerLogCommand : BaseCommand<ShowLanguageServerLogCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        if (await VS.GetMefServiceAsync<IDamlLogger>() is OutputWindowLogger logger)
        {
            await logger.ShowAsync(DamlLogCategory.LanguageServer);
        }
    }
}

/// <summary>Opens the Daml documentation in the browser.</summary>
[Command(DamlMenuIds.CommandSetString, DamlMenuIds.OpenDocumentation)]
internal sealed class OpenDocumentationCommand : BaseCommand<OpenDocumentationCommand>
{
    protected override void Execute(object sender, EventArgs e) => VsShellUtilities.OpenSystemBrowser(DamlMenuIds.DocumentationUrl);
}

/// <summary>Builds the package of the active Daml file.</summary>
[Command(DamlMenuIds.CommandSetString, DamlMenuIds.BuildCurrentPackage)]
internal sealed class BuildCurrentPackageCommand : BaseCommand<BuildCurrentPackageCommand>
{
    protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => CurrentPackage.RunAsync(DpmCommandKind.Build);
}

/// <summary>Runs the tests of the active Daml file's package.</summary>
[Command(DamlMenuIds.CommandSetString, DamlMenuIds.TestCurrentPackage)]
internal sealed class TestCurrentPackageCommand : BaseCommand<TestCurrentPackageCommand>
{
    protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => CurrentPackage.RunAsync(DpmCommandKind.Test);
}

/// <summary>
/// Runs dpm for the package the active Daml file belongs to, through the same service as the
/// Open Folder actions. Its output goes to the EQ Daml Tools: Build pane, followed by one line per
/// diagnostic that the Output window can jump from.
/// </summary>
internal static class CurrentPackage
{
    // The command in flight; choosing the command again cancels it and starts over.
    private static CancellationTokenSource? s_running;

    public static async Task RunAsync(DpmCommandKind kind)
    {
        var view = await VS.Documents.GetActiveDocumentViewAsync();
        var files = new PhysicalFileSystem();
        var action = DamlBuildActions.ForPackageOf(new WorkspaceLocator(files, new YamlManifestReader(files)), view?.FilePath, kind);
        if (action is null)
        {
            await VS.StatusBar.ShowMessageAsync($"EQ Daml: open a .daml file of a package (with a daml.yaml above it) to {kind.ToString().ToLowerInvariant()} it.");
            return;
        }

        var logger = await VS.GetMefServiceAsync<IDamlLogger>();
        var service = await VS.GetMefServiceAsync<DamlBuildService>();
        var output = logger as OutputWindowLogger;
        if (output is not null)
        {
            await output.ShowAsync(DamlLogCategory.Build);
        }
        var package = Path.GetFileName(action.WorkingDirectory);
        logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, $"{action.DisplayName} Current Package chosen from the Daml menu for {view!.FilePath}.");
        await VS.StatusBar.ShowMessageAsync($"EQ Daml: {action.DisplayName} {package}… (choose the command again to cancel)");

        await TaskScheduler.Default;
        using var cancellation = new CancellationTokenSource();
        try
        {
            // The previous run may be between finishing and disposing its source.
            Interlocked.Exchange(ref s_running, cancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            var run = await service.RunAsync(action, cancellation.Token);
            foreach (var diagnostic in run.Diagnostics)
            {
                output?.WritePlainLine(DamlLogCategory.Build, diagnostic.ToOutputLine());
            }
            await VS.StatusBar.ShowMessageAsync($"EQ Daml: {action.DisplayName} {package} {(run.Succeeded ? "succeeded" : "failed")}.");
        }
        catch (OperationCanceledException)
        {
            await VS.StatusBar.ShowMessageAsync($"EQ Daml: {action.DisplayName} {package} cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Log(DamlLogCategory.Build, DamlLogLevel.Error, $"{action.DisplayName} of {package} failed to run.", ex);
        }
        finally
        {
            Interlocked.CompareExchange(ref s_running, null, cancellation);
        }
    }
}
