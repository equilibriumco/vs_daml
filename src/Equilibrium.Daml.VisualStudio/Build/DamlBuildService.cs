using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.VisualStudio.Core.Build;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Sdk;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Equilibrium.Daml.VisualStudio.Build;

/// <summary>
/// The one place Open Folder build actions run through: dpm output goes to the "EQ Daml Tools: Build"
/// pane, and the diagnostics go back to the caller for Visual Studio's build reporting.
/// Closing the folder or solution cancels what's running.
/// </summary>
[Export(typeof(DamlBuildService))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class DamlBuildService : IDisposable
{
    private readonly DamlBuildCoordinator _coordinator;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Action<string> _onFolderClosing;

    [ImportingConstructor]
    public DamlBuildService(IDamlLogger logger, JoinableTaskContext joinableTaskContext)
    {
        _joinableTaskFactory = joinableTaskContext.Factory;
        _coordinator = new DamlBuildCoordinator(
            new DpmRunner(new ProcessRunner()),
            () => new AssistantLocator().Find(AssistantKind.Dpm, configuredPath: null)?.Path,
            logger);
        _onFolderClosing = _ => _coordinator.CancelAll();

        // Solution events have to be subscribed on the UI thread; nothing waits for it. Open
        // Folder raises the folder pair; the solution pair is raised for folders too.
        _joinableTaskFactory.RunAsync(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            VS.Events.SolutionEvents.OnBeforeCloseFolder += _onFolderClosing;
            VS.Events.SolutionEvents.OnBeforeCloseSolution += _coordinator.CancelAll;
        }).FileAndForget("daml/build/solutionEvents");
    }

    /// <summary>Runs one dpm command; see <see cref="DamlBuildCoordinator.RunAsync"/>.</summary>
    public Task<DamlBuildRun> RunAsync(DamlBuildAction action, CancellationToken cancellationToken) =>
        _coordinator.RunAsync(action, cancellationToken);

    public void Dispose()
    {
        // MEF may dispose this part from any thread; the events are a UI-thread object.
        if (ThreadHelper.CheckAccess())
        {
            Unsubscribe();
        }
        else
        {
            _joinableTaskFactory.RunAsync(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                Unsubscribe();
            }).FileAndForget("daml/build/unsubscribe");
        }
        _coordinator.Dispose();
    }

    private void Unsubscribe()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VS.Events.SolutionEvents.OnBeforeCloseFolder -= _onFolderClosing;
        VS.Events.SolutionEvents.OnBeforeCloseSolution -= _coordinator.CancelAll;
    }
}
