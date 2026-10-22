using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.Diagnostics;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageClient;
using Daml.VisualStudio.Logging;
using Daml.VisualStudio.Options;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Daml.VisualStudio.Commands;

/// <summary>
/// Writes a masked diagnostic report (versions, settings, recent logs) and opens it, so the user
/// reads what they would share before attaching it to a bug report.
/// </summary>
[Command(DamlMenuIds.CommandSetString, DamlMenuIds.ExportDiagnostics)]
internal sealed class ExportDiagnosticsCommand : BaseCommand<ExportDiagnosticsCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var logger = await VS.GetMefServiceAsync<IDamlLogger>();
        var output = logger as OutputWindowLogger;
        var settings = await DamlOptions.GetSettingsAsync();
        var host = await VS.Shell.GetVsVersionAsync();
        var workspace = (await VS.GetMefServiceAsync<DamlLanguageClient>()).Host.Workspace?.WorkspaceRoot;
        var input = new DiagnosticReportInput
        {
            ExtensionVersion = typeof(DamlPackage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            HostVersion = host?.ToString() ?? "unknown",
            Settings = settings,
            LogFolder = output?.LogFolder ?? "",
        };

        await TaskScheduler.Default;
        string path;
        try
        {
            if (output is not null)
            {
                await output.DrainFilesAsync();
            }
            var redactor = DiagnosticRedactor.ForCurrentUser(workspace is null ? [] : [workspace]);
            path = DiagnosticReport.Write(DiagnosticReport.DefaultFolder, input, redactor, TimeProvider.System);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, "Couldn't write the Daml diagnostic report.", ex);
            await VS.StatusBar.ShowMessageAsync("Daml: couldn't write the diagnostic report; the Daml pane of the Output window says why.");
            return;
        }

        logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Diagnostic report written to {path}.");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await VS.Documents.OpenAsync(path);
        await VS.StatusBar.ShowMessageAsync("Daml diagnostic report created. Read it before you share it.");
    }
}
