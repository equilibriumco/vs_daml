using System;
using System.IO;
using Daml.Build.Core.Building;
using Daml.Build.Core.Codegen;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

/// <summary>
/// Where a package's DAR came from, for the projects that generate code from it: the main package
/// id from the DAR's own manifest and the fingerprint of the sources its build recorded. Only
/// reads; nothing runs, so design-time builds can ask too.
/// </summary>
public sealed class DamlReadDarProvenance : Task
{
    /// <summary>The package's DAR.</summary>
    [Required]
    public string DarPath { get; set; } = "";

    /// <summary>Where the package's build recorded what the DAR was built from.</summary>
    [Required]
    public string StatePath { get; set; } = "";

    /// <summary>The id of the package the DAR is for; empty when there is no readable DAR.</summary>
    [Output]
    public string MainPackageId { get; private set; } = "";

    /// <summary>The sources the DAR was built from; empty when this build didn't make the DAR there.</summary>
    [Output]
    public string SourceFingerprint { get; private set; } = "";

    /// <inheritdoc />
    public override bool Execute()
    {
        var dar = Path.GetFullPath(DarPath);
        if (!File.Exists(dar))
        {
            return true;
        }
        try
        {
            MainPackageId = DarProvenance.Read(dar).MainPackageId;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Whoever generates code from it says what is wrong with it.
            Log.LogMessage(MessageImportance.Low, ex.Message);
        }
        SourceFingerprint = PackageBuildState.RecordedSourceFingerprint(Path.GetFullPath(StatePath), dar) ?? "";
        return true;
    }
}
