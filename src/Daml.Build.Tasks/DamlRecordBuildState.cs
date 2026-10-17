using System;
using System.IO;
using Daml.Build.Core.Building;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

/// <summary>
/// Records, once dpm built the DAR, what it was built from, so the next build can tell it is
/// current. A record that can't be written only costs the next build a rebuild.
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

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            new PackageBuildState(Fingerprint, SourceFingerprint).Record(Path.GetFullPath(StatePath), DarPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning($"What {DarPath} was built from couldn't be recorded in {StatePath} ({ex.Message}); the next build builds it again.");
        }
        return true;
    }
}
