using System.Collections.Generic;
using System.IO;
using Daml.Build.Core;
using Daml.Build.Core.Building;
using Microsoft.Build.Framework;

namespace Daml.Build.Tasks;

/// <summary>
/// Builds a package's DAR with <c>dpm build</c> and checks it landed where the manifest says,
/// since projects that use the DAR look for it there.
/// </summary>
public sealed class DamlBuildTask : DpmToolTask
{
    /// <summary>Where the manifest says the DAR goes, as <see cref="ReadDamlManifest"/> found it.</summary>
    [Required]
    public string DarPath { get; set; } = "";

    /// <summary>The DAR the build produced; empty when it failed.</summary>
    [Output]
    public string? CreatedDar { get; private set; }

    /// <inheritdoc />
    protected override DpmCommandKind Command => DpmCommandKind.Build;

    /// <inheritdoc />
    protected override void Completed(DpmOutcome outcome, int exitCode)
    {
        if (exitCode != 0)
        {
            return;
        }
        var problems = new List<BuildMessage>();
        CreatedDar = DarConfirmation.Confirm(outcome, Path.GetFullPath(DarPath), FullManifestPath, File.Exists, problems);
        foreach (var problem in problems)
        {
            Log.Report(problem);
        }
    }
}
