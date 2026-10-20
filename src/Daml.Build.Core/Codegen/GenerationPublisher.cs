using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Codegen;

/// <summary>What <see cref="GenerationPublisher.Recover"/> found and did.</summary>
public enum RecoveryOutcome
{
    /// <summary>Nothing was left half done.</summary>
    Nothing,

    /// <summary>Leftovers that were never published (a staging, a backup past its commit) were removed.</summary>
    Cleaned,

    /// <summary>A publish was cut short before the new generation landed; the previous one is back.</summary>
    RolledBack,

    /// <summary>A publish was cut short after the new generation landed; it is finished.</summary>
    Completed,
}

/// <summary>
/// Publishes a staged generation using a journal and backup so failed moves can be rolled back.
/// <see cref="Recover"/> handles interrupted publishes before the next real build.
/// Work stays on the same volume under the generation root, with short names derived from the
/// generation ID: <c>.new</c>, <c>.old</c>, <c>.journal</c> and <c>.run</c>.
/// Callers must hold the generation root's lock throughout recovery, staging and publication.
/// </summary>
public sealed class GenerationPublisher
{
    /// <summary>The work folder's name inside a generation root.</summary>
    public const string WorkFolderName = ".daml-work";

    private const string RunSuffix = ".run";

    private readonly string _root;
    private readonly IPublishSteps _steps;

    /// <summary>A publisher for the generations under <paramref name="root"/>.</summary>
    public GenerationPublisher(string root)
        : this(root, FileSystemPublishSteps.Instance)
    {
    }

    internal GenerationPublisher(string root, IPublishSteps steps)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _steps = steps;
    }

    private string Work => Path.Combine(_root, WorkFolderName);

    /// <summary>
    /// Makes the empty staging folder for a new generation of <paramref name="folderName"/>. Call
    /// <see cref="Recover"/> first.
    /// </summary>
    /// <exception cref="InvalidOperationException">A staging folder of an earlier run is still there.</exception>
    public string NewStaging(string folderName)
    {
        EnsureWork();
        var staging = Staging(folderName);
        if (Directory.Exists(staging))
        {
            throw new InvalidOperationException($"{staging} is still there; recover {folderName} before generating it again.");
        }
        Directory.CreateDirectory(staging);
        return staging;
    }

    /// <summary>
    /// The folder next to <paramref name="staging"/> for whatever the generator itself leaves
    /// while it runs; <see cref="Recover"/> removes it with the staging.
    /// </summary>
    public static string RunFolderOf(string staging) => Path.ChangeExtension(staging ?? throw new ArgumentNullException(nameof(staging)), RunSuffix);

    /// <summary>
    /// Whether a publish of <paramref name="folderName"/> was cut short and waits for a real
    /// build's <see cref="Recover"/>; only reads.
    /// </summary>
    public bool HasUnfinishedPublish(string folderName) => File.Exists(Journal(folderName));

    /// <summary>The folders under the root named like a generation's, whoever they belong to.</summary>
    public IReadOnlyList<string> FolderNames() =>
        Directory.Exists(_root)
            ? Directory.GetDirectories(_root)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name != WorkFolderName && GenerationIdentity.TryIdOfFolder(name) is not null)
                .ToList()
            : [];

    /// <summary>The generation ids the work folder holds entries for.</summary>
    public IReadOnlyList<string> WorkIds() =>
        Directory.Exists(Work)
            ? Directory.GetFileSystemEntries(Work)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Select(name => name.Split('.')[0])
                .Where(GenerationIdentity.IsId)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>
    /// Removes the generation <paramref name="folderName"/> of <paramref name="owner"/>: the files its
    /// manifest lists inside its folder, the manifest, the folders left empty and its entries in the
    /// work folder. Anything else stays: files the generator didn't write, and a generation
    /// another project's manifest claims. Without a manifest nothing in the folder is provably
    /// the generator's, so only empty folders go. Run under the root's lock.
    /// </summary>
    /// <exception cref="IOException">A file or folder couldn't be deleted.</exception>
    public GenerationRemoval Remove(string folderName, string owner)
    {
        var folder = Path.Combine(_root, folderName);
        var kept = new List<string>();
        if (Directory.Exists(folder))
        {
            var manifestPath = Path.Combine(folder, GenerationManifest.FileName);
            var manifest = GenerationManifest.Read(manifestPath);
            if (manifest is not null && !DamlPath.Comparer.Equals(manifest.Owner, owner))
            {
                return new GenerationRemoval(Owned: false, []);
            }
            if (manifest is not null)
            {
                foreach (var file in manifest.Files.Where(f => IsInside(f.Path)))
                {
                    var path = LongPaths.Safe(Path.Combine(folder, file.Path));
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                File.Delete(manifestPath);
            }
            kept.AddRange(LongPaths.RelativeFiles(folder));
            foreach (var empty in LongPaths.RelativeFolders(folder).OrderByDescending(f => f.Length).Append(""))
            {
                var path = LongPaths.Safe(Path.Combine(folder, empty).TrimEnd(Path.DirectorySeparatorChar));
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                }
            }
        }
        RemoveWork(GenerationIdentity.IdOfFolder(folderName));
        return new GenerationRemoval(Owned: true, kept);
    }

    /// <summary>Removes the work folder's entries for the generation <paramref name="id"/>: staging, backup, journal and run folder.</summary>
    /// <exception cref="IOException">An entry couldn't be deleted.</exception>
    public void RemoveWork(string id)
    {
        foreach (var folder in new[] { ".new", ".old", RunSuffix }.Select(suffix => Path.Combine(Work, id + suffix)))
        {
            DeleteIfThere(folder);
        }
        var journal = Path.Combine(Work, id + ".journal");
        if (File.Exists(journal))
        {
            File.Delete(journal);
        }
    }

    /// <summary>Removes the work folder when nothing but its own <c>.gitignore</c> is left, and then the root when it is empty.</summary>
    public void RemoveIfEmpty()
    {
        if (Directory.Exists(Work) && Directory.GetFileSystemEntries(Work).All(e => Path.GetFileName(e) == ".gitignore"))
        {
            _steps.Delete(Work);
        }
        if (Directory.Exists(_root) && !Directory.EnumerateFileSystemEntries(_root).Any())
        {
            Directory.Delete(_root);
        }
    }

    // A manifest names files inside its folder; a rooted path or one that climbs out is no file of the generation's.
    private static bool IsInside(string relative) =>
        relative.Length > 0
        && !Path.IsPathRooted(relative)
        && relative.IndexOf(':') < 0
        && !relative.Split('\\', '/').Any(segment => segment == "..");

    /// <summary>Removes a staging folder that won't be published.</summary>
    public void Discard(string staging)
    {
        if (Directory.Exists(staging))
        {
            _steps.Delete(staging);
        }
    }

    /// <summary>
    /// Publishes <paramref name="staging"/> as <paramref name="folderName"/>. Call <see cref="Recover"/> first.
    /// </summary>
    /// <exception cref="GenerationPublishException">
    /// The publish didn't happen. With DAML0104 the previous generation is in place; with DAML0109
    /// it couldn't be put back and stays in the backup, which the next <see cref="Recover"/> restores.
    /// </exception>
    public void Publish(string folderName, string staging)
    {
        EnsureWork();
        var active = Path.Combine(_root, folderName);
        var backup = Backup(folderName);
        var journal = Journal(folderName);
        if (File.Exists(journal))
        {
            throw new InvalidOperationException($"{journal} is still there; recover {folderName} before publishing it.");
        }

        var stagingName = Path.GetFileName(staging);
        try
        {
            _steps.Write(journal, Entry(stagingName, "staged"));
        }
        catch (IOException ex)
        {
            throw Failed(folderName, $"its journal couldn't be written ({ex.Message}); the previous generation is unchanged", ex);
        }

        var hadOld = Directory.Exists(active);
        if (hadOld)
        {
            try
            {
                _steps.Move(active, backup);
            }
            catch (IOException ex)
            {
                TryDeleteFile(journal);
                throw Failed(folderName, $"the previous generation couldn't be moved aside ({ex.Message}); it is unchanged", ex);
            }
        }
        // Recovery goes by what is on disk; the phase only tells a person reading the journal.
        TryWrite(journal, Entry(stagingName, "old-moved"));

        try
        {
            _steps.Move(staging, active);
        }
        catch (IOException ex)
        {
            if (!hadOld)
            {
                TryDeleteFile(journal);
                throw Failed(folderName, $"it couldn't be moved into place ({ex.Message})", ex);
            }
            try
            {
                _steps.Move(backup, active);
            }
            catch (IOException restore)
            {
                throw new GenerationPublishException(
                    DamlDiagnosticCodes.RollbackFailed,
                    $"Publishing the generation {folderName} failed ({ex.Message}), and the previous generation couldn't be put back ({restore.Message}). " +
                    $"It is kept at {backup}; the next build puts it back.",
                    restore);
            }
            TryDeleteFile(journal);
            throw Failed(folderName, $"it couldn't be moved into place ({ex.Message}); the previous generation is back in place", ex);
        }

        // Committed: the new generation is in place. What is left is only tidying, which the
        // next recovery finishes when it fails here.
        TryWrite(journal, Entry(stagingName, "new-moved"));
        if (hadOld)
        {
            TouchChanged(active, backup);
        }
        if (Directory.Exists(backup))
        {
            try
            {
                _steps.Delete(backup);
            }
            catch (IOException)
            {
                // Left for the next recovery; nothing ever compiles from the work folder.
            }
        }
        TryDeleteFile(journal);
    }

    /// <summary>
    /// Finishes or undoes what a publish of <paramref name="folderName"/> cut short, judging by
    /// what is on disk: if the staged generation never landed, the previous one goes back;
    /// if it landed, the backup goes. Stagings of generators that died before publishing are
    /// removed. Run under the root's lock, before the generation is looked at.
    /// </summary>
    public RecoveryOutcome Recover(string folderName)
    {
        var active = Path.Combine(_root, folderName);
        var backup = Backup(folderName);
        var journal = Journal(folderName);
        var outcome = RecoveryOutcome.Nothing;
        if (File.Exists(journal))
        {
            // The generation ID determines the only staging folder we own. A damaged journal
            // must never redirect recursive cleanup to another path.
            var staging = Staging(folderName);
            if (Directory.Exists(staging))
            {
                // The new generation never landed.
                _steps.Delete(staging);
                if (!Directory.Exists(active) && Directory.Exists(backup))
                {
                    _steps.Move(backup, active);
                    outcome = RecoveryOutcome.RolledBack;
                }
                else
                {
                    DeleteIfThere(backup);
                    outcome = RecoveryOutcome.Cleaned;
                }
            }
            else if (Directory.Exists(active))
            {
                DeleteIfThere(backup);
                outcome = RecoveryOutcome.Completed;
            }
            else if (Directory.Exists(backup))
            {
                _steps.Move(backup, active);
                outcome = RecoveryOutcome.RolledBack;
            }
            else
            {
                outcome = RecoveryOutcome.Cleaned;
            }
            File.Delete(journal);
        }
        else if (Directory.Exists(backup))
        {
            _steps.Delete(backup);
            outcome = RecoveryOutcome.Cleaned;
        }

        // A generator killed before its publish leaves its staging and its run folder.
        var leftStaging = Staging(folderName);
        foreach (var orphan in new[] { leftStaging, RunFolderOf(leftStaging) })
        {
            if (Directory.Exists(orphan))
            {
                _steps.Delete(orphan);
                outcome = outcome == RecoveryOutcome.Nothing ? RecoveryOutcome.Cleaned : outcome;
            }
        }
        return outcome;
    }

    private string Staging(string folderName) => Path.Combine(Work, GenerationIdentity.IdOfFolder(folderName) + ".new");

    private string Backup(string folderName) => Path.Combine(Work, GenerationIdentity.IdOfFolder(folderName) + ".old");

    private string Journal(string folderName) => Path.Combine(Work, GenerationIdentity.IdOfFolder(folderName) + ".journal");

    private static string Entry(string staging, string phase) => $"staging={staging}\nphase={phase}\n";

    // Inside a Source folder the work folder sits in the project's tree; git never needs it.
    private void EnsureWork()
    {
        Directory.CreateDirectory(Work);
        var ignore = Path.Combine(Work, ".gitignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, "*\n");
        }
    }

    private void DeleteIfThere(string folder)
    {
        if (Directory.Exists(folder))
        {
            _steps.Delete(folder);
        }
    }

    private void TryWrite(string file, string text)
    {
        try
        {
            _steps.Write(file, text);
        }
        catch (IOException)
        {
            // Only the record of the commit; the folders already say it happened.
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // A journal left behind is read by the next recovery, which finds nothing to undo.
        }
    }

    // The new generation came in as a whole folder, which file watchers see only as folders
    // renamed: Visual Studio would go on with the old text of every file whose path stays the
    // same. Touching the files that are new or changed tells them; the others keep their time, so
    // nothing compiles again because of them. Best effort: the build itself reads the files anyway.
    private static void TouchChanged(string active, string backup)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var file in Directory.EnumerateFiles(active, "*", SearchOption.AllDirectories))
            {
                var before = Path.Combine(backup, file.Substring(active.Length + 1));
                if (!File.Exists(before) || !SameContent(file, before))
                {
                    File.SetLastWriteTimeUtc(file, now);
                }
                else
                {
                    File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(before));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            // A file past the path limit or held open keeps its time; it was written moments ago.
        }
    }

    private static bool SameContent(string first, string second) =>
        new FileInfo(first).Length == new FileInfo(second).Length && Hashing.Sha256File(first) == Hashing.Sha256File(second);

    private static GenerationPublishException Failed(string folderName, string what, Exception inner) =>
        new(DamlDiagnosticCodes.CodegenFailed, $"Publishing the generation {folderName} failed: {what}.", inner);
}
