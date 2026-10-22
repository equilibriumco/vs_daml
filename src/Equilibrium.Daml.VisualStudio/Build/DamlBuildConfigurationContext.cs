using System;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Build;
using Microsoft.VisualStudio.Workspace.Build;

namespace Equilibrium.Daml.VisualStudio.Build;

/// <summary>
/// The context of a Daml manifest's file contexts. For Build and Clean it is what the folder
/// view's own Build and Clean commands run: Open Folder enables them only for a build file
/// context whose configuration matches one indexed for the file. Both sides use Default
/// for the package's single configuration.
/// </summary>
internal sealed class DamlBuildConfigurationContext : IBuildFileContext
{
    private readonly Func<DamlBuildAction, CancellationToken, Task<DamlBuildRun>> _run;

    public DamlBuildConfigurationContext(DamlBuildAction action, Func<DamlBuildAction, CancellationToken, Task<DamlBuildRun>> run)
    {
        Action = action;
        _run = run;
    }

    /// <summary>The dpm command the context stands for.</summary>
    public DamlBuildAction Action { get; }

    /// <inheritdoc />
    /// <summary>
    /// The one configuration a Daml package has. Named, not null, so Visual Studio's own build
    /// commands, which match it against the folder's current project setting, have something
    /// to match.
    /// </summary>
    public const string ConfigurationName = "Default";

    public string? BuildConfiguration => ConfigurationName;

    /// <summary>Runs the command and reports its diagnostics as build messages.</summary>
    public async Task<bool> ExecuteBuildAsync(IBuildActionProgress progress, CancellationToken cancellationToken)
    {
        var run = await _run(Action, cancellationToken).ConfigureAwait(false);
        if (progress is not null)
        {
            foreach (var message in BuildMessages.From(run.Diagnostics, Action.ManifestPath))
            {
                await progress.ReportAsync(message, null).ConfigureAwait(false);
            }
        }
        return run.Succeeded;
    }
}
