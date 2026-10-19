using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.Build.Core;
using Daml.Build.Core.Building;
using Microsoft.Build.Framework;

namespace Daml.Build.Tasks;

/// <summary>
/// Builds a package's DAR with <c>dpm build</c> and checks it landed where the manifest says,
/// since projects that use the DAR look for it there. Given the state <c>DamlComputeBuildState</c>
/// found, it also records what the DAR was built from, before another build of the package can
/// look; and a DAR another build made from the same inputs while this one waited isn't built again.
/// </summary>
public sealed class DamlBuildTask : DpmToolTask
{
    /// <summary>Where the manifest says the DAR goes, as <see cref="ReadDamlManifest"/> found it.</summary>
    [Required]
    public string DarPath { get; set; } = "";

    /// <summary>Where the build records what the DAR was built from; without it nothing is recorded.</summary>
    public string? StatePath { get; set; }

    /// <summary>What <c>DamlComputeBuildState</c> found the DAR depends on before the build.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>The sources' part of it.</summary>
    public string? SourceFingerprint { get; set; }

    /// <summary>The package's source folder or file, as given to <c>DamlComputeBuildState</c>.</summary>
    public string? SourcePath { get; set; }

    /// <summary>The package's <c>dependencies</c>, as given to <c>DamlComputeBuildState</c>.</summary>
    public ITaskItem[] Dependencies { get; set; } = [];

    /// <summary>The package's <c>data-dependencies</c>, as given to <c>DamlComputeBuildState</c>.</summary>
    public ITaskItem[] DataDependencies { get; set; } = [];

    /// <summary>The DAR the build produced; empty when it failed or another build made it.</summary>
    [Output]
    public string? CreatedDar { get; private set; }

    /// <inheritdoc />
    protected override DpmCommandKind Command => DpmCommandKind.Build;

    private PackageBuildState? Before =>
        string.IsNullOrEmpty(StatePath) || string.IsNullOrEmpty(Fingerprint) || string.IsNullOrEmpty(SourcePath)
            ? null
            : new PackageBuildState(Fingerprint!, SourceFingerprint ?? "");

    /// <inheritdoc />
    protected override bool DoneWhileWaiting()
    {
        if (Before is not { } before || !before.IsRecordedIn(StatePath!, DarPath))
        {
            return false;
        }
        Log.LogMessage(MessageImportance.Normal, $"{DarPath} was built from the same inputs by another build while this one waited for it, so dpm doesn't build it again.");
        return true;
    }

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
        if (CreatedDar is not null && Before is { } before)
        {
            Record(before);
        }
    }

    // Still holding the package, so a build waiting for it finds the record.
    private void Record(PackageBuildState before)
    {
        try
        {
            var dars = Dependencies.Select(d => d.GetMetadata("DarPath")).Where(d => d.Length > 0)
                .Concat(DataDependencies.Select(d => d.ItemSpec))
                .Select(Path.GetFullPath);
            if (!before.RecordIfUnchanged(FullManifestPath, SourcePath!, dars, Path.GetFullPath(StatePath!), DarPath))
            {
                Log.LogMessage(MessageImportance.High, $"What {DarPath} is built from changed while dpm built it, so the next build builds it again.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning($"What {DarPath} was built from couldn't be recorded in {StatePath} ({ex.Message}); the next build builds it again.");
        }
    }
}
