using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.Build;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Sdk;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Threading;

namespace Daml.VisualStudio.Build;

/// <summary>
/// The one place Open Folder build actions run through: dpm output goes to the "Daml Build"
/// pane, diagnostics to the Error List. Closing the folder or solution cancels what's running.
/// </summary>
[Export(typeof(DamlBuildService))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class DamlBuildService : IDisposable
{
    private readonly DamlBuildCoordinator _coordinator;
    private readonly BuildErrorListSource _errorList;
    private readonly ITableManager _errorsTable;

    [ImportingConstructor]
    public DamlBuildService(IDamlLogger logger, ITableManagerProvider tableManagerProvider, JoinableTaskContext joinableTaskContext)
    {
        Store = new BuildDiagnosticStore();
        _coordinator = new DamlBuildCoordinator(
            new DpmRunner(new ProcessRunner()),
            () => new AssistantLocator().Find(AssistantKind.Dpm, configuredPath: null)?.Path,
            Store,
            logger);

        _errorList = new BuildErrorListSource(Store);
        _errorsTable = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);
        _errorsTable.AddSource(_errorList, BuildErrorListSource.Columns);

        // Solution events have to be subscribed on the UI thread; nothing waits for it.
        _ = joinableTaskContext.Factory.RunAsync(async () =>
        {
            await joinableTaskContext.Factory.SwitchToMainThreadAsync();
            VS.Events.SolutionEvents.OnBeforeCloseSolution += _coordinator.CancelAll;
        });
    }

    /// <summary>What the Error List shows.</summary>
    public BuildDiagnosticStore Store { get; }

    /// <summary>Runs one dpm command; see <see cref="DamlBuildCoordinator.RunAsync"/>.</summary>
    public Task<bool> RunAsync(DamlBuildAction action, CancellationToken cancellationToken) =>
        _coordinator.RunAsync(action, cancellationToken);

    public void Dispose()
    {
        _errorsTable.RemoveSource(_errorList);
        _errorList.Dispose();
        _coordinator.Dispose();
    }
}
