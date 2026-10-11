using Daml.VisualStudio.Core.Build;
using Microsoft.VisualStudio.Workspace.Build;

namespace Daml.VisualStudio.Build;

/// <summary>
/// The context of a Daml manifest's file contexts. Open Folder only enables its Build and Clean
/// commands when the context is a build configuration matching one indexed for the file; Daml
/// packages have no configurations, so it's "not applicable" (<c>null</c>) here and in the
/// index alike.
/// </summary>
internal sealed class DamlBuildConfigurationContext : IBuildConfigurationContext
{
    public DamlBuildConfigurationContext(DamlBuildAction action) => Action = action;

    /// <summary>The dpm command the context stands for.</summary>
    public DamlBuildAction Action { get; }

    /// <inheritdoc />
    public string? BuildConfiguration => null;
}
