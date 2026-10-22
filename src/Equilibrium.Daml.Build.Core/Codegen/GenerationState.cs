using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>Whether a generation can be used as it is.</summary>
public enum GenerationStatus
{
    /// <summary>Published from the same inputs, every file intact: compile it as it is.</summary>
    Current,

    /// <summary>Missing, from other inputs, or with a file deleted or changed: generate it again.</summary>
    Stale,

    /// <summary>The folder holds files the generator didn't write, or another project's generation: the build stops rather than touch them.</summary>
    Conflict,
}

/// <summary>What a generation's folder holds, judged against the inputs of this build.</summary>
/// <param name="Status">Whether it can be used.</param>
/// <param name="Reason">Why it can't be used as it is; null when it can.</param>
/// <param name="Files">The full paths of its files when it is current, in manifest order.</param>
/// <param name="ForeignFiles">Files in the folder the manifest doesn't list, relative to it.</param>
public sealed record GenerationState(GenerationStatus Status, string? Reason, IReadOnlyList<string> Files, IReadOnlyList<string> ForeignFiles)
{
    private const int ListedFiles = 5;

    /// <summary>
    /// Judges the generation in <paramref name="folder"/>: its manifest must be of this build's
    /// schema, belong to <paramref name="owner"/>, have <paramref name="fingerprint"/>, and every
    /// file it lists must be there unchanged. A file it doesn't list is a conflict, never
    /// something to delete. Only reads.
    /// </summary>
    /// <param name="folder">The generation's folder.</param>
    /// <param name="fingerprint">The fingerprint of this build's inputs.</param>
    /// <param name="owner">The canonical path of the project asking.</param>
    public static GenerationState Evaluate(string folder, string fingerprint, string owner)
    {
        if (!Directory.Exists(folder ?? throw new ArgumentNullException(nameof(folder))))
        {
            return Stale("There is no generation yet.");
        }
        try
        {
            return Judge(folder, fingerprint, owner);
        }
        catch (IOException ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            // Before a build holds the output lock, a clean of another build can take the folder
            // away between the check above and the reads; the judgement under the lock settles it.
            return Stale("It was removed while this build looked at it.");
        }
    }

    private static GenerationState Judge(string folder, string fingerprint, string owner)
    {
        var manifestPath = Path.Combine(folder, GenerationManifest.FileName);
        var onDisk = LongPaths.RelativeFiles(folder)
            .Where(f => !string.Equals(f, GenerationManifest.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!File.Exists(manifestPath))
        {
            return onDisk.Count == 0 ? Stale("There is no generation yet.") : Foreign(folder, onDisk);
        }
        if (GenerationManifest.Read(manifestPath) is not { } manifest)
        {
            return new GenerationState(
                GenerationStatus.Conflict,
                $"{manifestPath} is incomplete or uses an unsupported schema, so ownership of {folder} cannot be established. " +
                "Restore its manifest or choose another DamlGeneratedDir; the build leaves these files untouched.",
                [],
                onDisk);
        }
        if (!DamlPath.Comparer.Equals(manifest.Owner, owner))
        {
            return new GenerationState(
                GenerationStatus.Conflict,
                $"{folder} holds bindings generated for {manifest.Owner}, not for {owner}. Give each project a {DamlProperties.GeneratedDir.Name} of its own.",
                [],
                []);
        }
        var listed = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var foreign = onDisk.Where(f => !listed.Contains(f)).ToList();
        if (foreign.Count > 0)
        {
            return Foreign(folder, foreign);
        }
        if (!string.Equals(manifest.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Stale("Its inputs changed.");
        }
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(folder, file.Path);
            if (!LongPaths.FileExists(path))
            {
                return Stale($"{file.Path} was deleted.");
            }
            if (!string.Equals(Hashing.Sha256File(path), file.Sha256, StringComparison.Ordinal))
            {
                return Stale($"{file.Path} was changed since it was generated.");
            }
        }
        return new GenerationState(GenerationStatus.Current, null, manifest.Files.Select(f => Path.Combine(folder, f.Path)).ToList(), []);
    }

    private static GenerationState Stale(string reason) => new(GenerationStatus.Stale, reason, [], []);

    private static GenerationState Foreign(string folder, List<string> files) => new(
        GenerationStatus.Conflict,
        $"{folder} has files the generator didn't write: {string.Join(", ", files.Take(ListedFiles))}{(files.Count > ListedFiles ? ", …" : "")}. " +
        $"The build never deletes them; move them out, or give {DamlProperties.GeneratedDir.Name} a folder of its own.",
        [],
        files);
}
