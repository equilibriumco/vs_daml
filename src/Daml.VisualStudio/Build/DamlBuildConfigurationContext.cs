using System;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Build;
using Microsoft.VisualStudio.Workspace.Build;

namespace Daml.VisualStudio.Build;

/// <summary>
/// The context of a Daml manifest's file contexts. For Build and Clean it is what the folder
/// view's own Build and Clean commands run: Open Folder enables them only for a build file
/// context whose configuration matches one indexed for the file. Daml packages have no
/// configurations, so it's "not applicable" (<c>null</c>) here and in the index alike.
/// </summary>
internal sealed class DamlBuildConfigurationContext : IBuildFileContext
{
    private readonly Func<DamlBuildAction, CancellationToken, Task<bool>> _run;

    public DamlBuildConfigurationContext(DamlBuildAction action, Func<DamlBuildAction, CancellationToken, Task<bool>> run)
    {
        Action = action;
        _run = run;
    }

    /// <summary>The dpm command the context stands for.</summary>
    public DamlBuildAction Action { get; }

    /// <inheritdoc />
    public string? BuildConfiguration => null;

    /// <inheritdoc />
    public Task<bool> ExecuteBuildAsync(IBuildActionProgress progress, CancellationToken cancellationToken) => _run(Action, cancellationToken);
}
