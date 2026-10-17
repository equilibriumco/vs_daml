using System;
using System.IO;
using System.Linq;
using Daml.Build.Core.Building;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

/// <summary>
/// Records, once dpm built the DAR, what it was built from, so the next build can tell it is
/// current. If the inputs changed while dpm ran, the DAR may hold some of the old ones, so nothing
/// is recorded and the next build builds again; a record that can't be written costs the same.
/// </summary>
public sealed class DamlRecordBuildState : Task
{
    /// <summary>Where to record it.</summary>
    [Required]
    public string StatePath { get; set; } = "";

    /// <summary>The DAR just built.</summary>
    [Required]
    public string DarPath { get; set; } = "";

    /// <summary>What <c>DamlComputeBuildState</c> computed before the build.</summary>
    [Required]
    public string Fingerprint { get; set; } = "";

    /// <summary>The sources' part of it.</summary>
    [Required]
    public string SourceFingerprint { get; set; } = "";

    /// <summary>The package's <c>daml.yaml</c>, as given to <c>DamlComputeBuildState</c>.</summary>
    [Required]
    public string ManifestPath { get; set; } = "";

    /// <summary>The package's source folder or file, as given to <c>DamlComputeBuildState</c>.</summary>
    [Required]
    public string SourcePath { get; set; } = "";

    /// <summary>The package's <c>dependencies</c>, as given to <c>DamlComputeBuildState</c>.</summary>
    public ITaskItem[] Dependencies { get; set; } = [];

    /// <summary>The package's <c>data-dependencies</c>, as given to <c>DamlComputeBuildState</c>.</summary>
    public ITaskItem[] DataDependencies { get; set; } = [];

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            var dars = Dependencies.Select(d => d.GetMetadata("DarPath")).Where(d => d.Length > 0)
                .Concat(DataDependencies.Select(d => d.ItemSpec))
                .Select(Path.GetFullPath);
            var now = PackageBuildState.Compute(Path.GetFullPath(ManifestPath), SourcePath, dars);
            if (now.Fingerprint != Fingerprint)
            {
                Log.LogMessage(MessageImportance.High, $"What {DarPath} is built from changed while dpm built it, so the next build builds it again.");
                return true;
            }
            new PackageBuildState(Fingerprint, SourceFingerprint).Record(Path.GetFullPath(StatePath), DarPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning($"What {DarPath} was built from couldn't be recorded in {StatePath} ({ex.Message}); the next build builds it again.");
        }
        return true;
    }
}
