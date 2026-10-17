using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.Build.Core.Building;
using Daml.Build.Core.Configuration;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

/// <summary>
/// Decides, before the build would skip anything, whether a Daml package needs building: its DAR
/// has to exist and to have been built from the manifest, the sources and the dependency DARs as
/// they are now. Nothing runs and nothing is written.
/// </summary>
public sealed class DamlComputeBuildState : Task
{
    /// <summary>The package's <c>daml.yaml</c>.</summary>
    [Required]
    public string ManifestPath { get; set; } = "";

    /// <summary>The package's source folder or file, as <c>ReadDamlManifest</c> resolved it.</summary>
    [Required]
    public string SourcePath { get; set; } = "";

    /// <summary>The DAR the build makes.</summary>
    [Required]
    public string DarPath { get; set; } = "";

    /// <summary>Where the last build recorded what it was built from.</summary>
    [Required]
    public string StatePath { get; set; } = "";

    /// <summary>The package's <c>dependencies</c>; those that are DARs carry <c>DarPath</c>.</summary>
    public ITaskItem[] Dependencies { get; set; } = [];

    /// <summary>The package's <c>data-dependencies</c>, each a DAR.</summary>
    public ITaskItem[] DataDependencies { get; set; } = [];

    /// <summary><c>DamlBuildEnabled</c>.</summary>
    public string? BuildEnabled { get; set; }

    /// <summary>Whether dpm has to build the package.</summary>
    [Output]
    public bool ShouldBuild { get; private set; }

    /// <summary>Everything the DAR depends on, to record once it is built.</summary>
    [Output]
    public string Fingerprint { get; private set; } = "";

    /// <summary>The sources alone: what a DAR built now is built from.</summary>
    [Output]
    public string SourceFingerprint { get; private set; } = "";

    /// <inheritdoc />
    public override bool Execute()
    {
        var manifest = Path.GetFullPath(ManifestPath);
        var errors = new List<ConfigurationError>();
        var options = PackageOptions.Read(name => name == DamlProperties.BuildEnabled.Name ? BuildEnabled : null, errors);
        foreach (var error in errors)
        {
            Log.LogError(null, error.Code, null, manifest, 0, 0, 0, 0, error.Message);
        }
        if (errors.Count > 0)
        {
            return false;
        }

        var dars = Dependencies.Select(d => d.GetMetadata("DarPath")).Where(d => d.Length > 0)
            .Concat(DataDependencies.Select(d => d.ItemSpec))
            .Select(Path.GetFullPath);
        var state = PackageBuildState.Compute(manifest, SourcePath, dars);
        Fingerprint = state.Fingerprint;
        SourceFingerprint = state.SourceFingerprint;
        if (!options.BuildEnabled)
        {
            Log.LogMessage(MessageImportance.Normal, $"{DamlProperties.BuildEnabled.Name} is off, so dpm doesn't build {DarPath}.");
            return true;
        }
        ShouldBuild = !state.IsRecordedIn(StatePath, DarPath);
        if (!ShouldBuild)
        {
            Log.LogMessage(MessageImportance.Normal, $"{DarPath} is up to date: nothing it is built from has changed.");
        }
        return true;
    }
}
