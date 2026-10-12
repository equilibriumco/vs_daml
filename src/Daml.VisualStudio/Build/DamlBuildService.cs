using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.Build;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Sdk;
using Microsoft.VisualStudio.Threading;

namespace Daml.VisualStudio.Build;

/// <summary>
/// The one place Open Folder build actions run through: dpm output goes to the "Daml Build"
/// pane, and the diagnostics go back to the caller for Visual Studio's build reporting.
/// Closing the folder or solution cancels what's running.
/// </summary>
[Export(typeof(DamlBuildService))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class DamlBuildService : IDisposable
{
    private readonly DamlBuildCoordinator _coordinator;

    [ImportingConstructor]
    public DamlBuildService(IDamlLogger logger, JoinableTaskContext joinableTaskContext)
    {
        _coordinator = new DamlBuildCoordinator(
            new DpmRunner(new ProcessRunner()),
            () => new AssistantLocator().Find(AssistantKind.Dpm, configuredPath: null)?.Path,
            logger);

        // Solution events have to be subscribed on the UI thread; nothing waits for it.
        _ = joinableTaskContext.Factory.RunAsync(async () =>
        {
            await joinableTaskContext.Factory.SwitchToMainThreadAsync();
            VS.Events.SolutionEvents.OnBeforeCloseSolution += _coordinator.CancelAll;
        });
    }

    /// <summary>Runs one dpm command; see <see cref="DamlBuildCoordinator.RunAsync"/>.</summary>
    public Task<DamlBuildRun> RunAsync(DamlBuildAction action, CancellationToken cancellationToken) =>
        _coordinator.RunAsync(action, cancellationToken);

    public void Dispose()
    {
        VS.Events.SolutionEvents.OnBeforeCloseSolution -= _coordinator.CancelAll;
        _coordinator.Dispose();
    }
}
