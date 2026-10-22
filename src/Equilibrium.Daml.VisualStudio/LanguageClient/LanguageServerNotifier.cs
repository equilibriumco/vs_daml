using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.LanguageClient;

/// <summary>
/// What the user sees of the language server: the status bar, the readiness UI context and the
/// info bars for problems and pending settings. Everything here is fire-and-forget onto the UI
/// thread; nothing waits for it.
/// </summary>
internal sealed class LanguageServerNotifier
{
    private const string RestartAction = "Restart";
    private const string ApplySettingsAction = "Restart now";

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;
    private readonly LanguageServerHost _host;

    public LanguageServerNotifier(JoinableTaskFactory joinableTaskFactory, IDamlLogger logger, LanguageServerHost host)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _logger = logger;
        _host = host;
    }

    /// <summary>A session is starting; like any status bar text, the next message replaces it.</summary>
    public void ShowStarting() =>
        _joinableTaskFactory.RunAsync(() => VS.StatusBar.ShowMessageAsync("Daml: starting…")).FileAndForget("daml/languageClient/starting");

    /// <summary>Publishes readiness as a UI context (commands and tests wait on it) and in the status bar.</summary>
    public void OnReadinessChanged(object? sender, EventArgs e) =>
        _joinableTaskFactory.RunAsync(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            // Read again on the UI thread, so a quick ready-then-stopped can't leave it stale.
            var ready = _host.IsReady;
            UIContext.FromUIContextGuid(PackageGuids.LanguageServerReady).IsActive = ready;
            if (ready)
            {
                await VS.StatusBar.ShowMessageAsync("Daml: ready");
            }
        }).FileAndForget("daml/languageClient/readiness");

    public void OnProblemRaised(object? sender, LanguageServerProblemEventArgs e) =>
        _joinableTaskFactory.RunAsync(() => ShowProblemAsync(e.Problem)).FileAndForget("daml/languageClient/showProblem");

    /// <summary>Settings that only a restart applies were saved.</summary>
    public void ShowRestartRequired(IReadOnlyList<string> changes) =>
        _joinableTaskFactory.RunAsync(() => ShowRestartRequiredAsync(changes)).FileAndForget("daml/options/restartRequired");

    private async Task ShowProblemAsync(LanguageServerProblem problem)
    {
        var actions = problem.Kind == LanguageServerProblemKind.CircuitOpen
            ? new[] { new InfoBarHyperlink(RestartAction) }
            : Array.Empty<InfoBarHyperlink>();
        var model = new InfoBarModel(
            new[] { new InfoBarTextSpan(problem.Message + " ") },
            actions,
            problem.Kind == LanguageServerProblemKind.NoWorkspace ? KnownMonikers.StatusInformation : KnownMonikers.StatusWarning,
            isCloseButtonVisible: true);
        var infoBar = await VS.InfoBar.CreateAsync(model);
        if (infoBar is null)
        {
            return;
        }
        infoBar.ActionItemClicked += (_, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // VS raises it from the info bar's UI
            if (args.ActionItem.Text == RestartAction)
            {
                args.InfoBarUIElement.Close();
                _host.Supervisor.ResetCircuit();
            }
        };
        await infoBar.TryShowInfoBarUIAsync();
    }

    private async Task ShowRestartRequiredAsync(IReadOnlyList<string> changes)
    {
        var model = new InfoBarModel(
            new[] { new InfoBarTextSpan($"Daml settings changed ({string.Join(", ", changes)}). Restart the language server to apply them. ") },
            new[] { new InfoBarHyperlink(ApplySettingsAction) },
            KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);
        var infoBar = await VS.InfoBar.CreateAsync(model);
        if (infoBar is null)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, "Couldn't show the restart bar for the changed settings; use Restart to apply them.");
            return;
        }
        infoBar.ActionItemClicked += (_, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // VS raises it from the info bar's UI
            if (args.ActionItem.Text == ApplySettingsAction)
            {
                args.InfoBarUIElement.Close();
                _host.Supervisor.RequestRestart("Options changed");
            }
        };
        if (!await infoBar.TryShowInfoBarUIAsync())
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, "Couldn't show the restart bar for the changed settings; use Restart to apply them.");
        }
    }
}
