using System;
using System.IO;
using System.Linq;
using Daml.Build.Core.Diagnostics;

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
/// Puts a generation that was produced in a staging folder in place of the published one.
/// Windows can't swap two folders atomically, so a publish is a sequence: a journal is written,
/// the old generation moves aside to a backup, the staged one moves in, and only then are the
/// backup and the journal removed. Any failed move puts the old generation back at once. A
/// process that dies in between leaves the journal and the folders telling the next real build
/// what happened, which <see cref="Recover"/> reads before anything else looks at the generation.
/// Staging, backup and journal live in a work folder inside the generation root: on the same
/// volume as the generations, so every move is a rename, and shared by every build writing to
/// that root, so any of them finds what another left behind.
/// </summary>
public sealed class GenerationPublisher
{
    /// <summary>The work folder's name inside a generation root.</summary>
    public const string WorkFolderName = ".daml-work";

    private const string StagingInfix = ".staging-";

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

    /// <summary>Makes an empty staging folder for a new generation of <paramref name="folderName"/>.</summary>
    public string NewStaging(string folderName)
    {
        EnsureWork();
        var staging = Path.Combine(Work, folderName + StagingInfix + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(staging);
        return staging;
    }

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
            var staging = File.ReadAllLines(journal)
                .Where(l => l.StartsWith("staging=", StringComparison.Ordinal))
                .Select(l => Path.Combine(Work, l.Substring("staging=".Length)))
                .FirstOrDefault();
            if (staging is not null && Directory.Exists(staging))
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

        if (Directory.Exists(Work))
        {
            foreach (var orphan in Directory.GetDirectories(Work, folderName + StagingInfix + "*"))
            {
                _steps.Delete(orphan);
                outcome = outcome == RecoveryOutcome.Nothing ? RecoveryOutcome.Cleaned : outcome;
            }
        }
        return outcome;
    }

    private string Backup(string folderName) => Path.Combine(Work, folderName + ".backup");

    private string Journal(string folderName) => Path.Combine(Work, folderName + ".journal");

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

    private static GenerationPublishException Failed(string folderName, string what, Exception inner) =>
        new(DamlDiagnosticCodes.CodegenFailed, $"Publishing the generation {folderName} failed: {what}.", inner);
}
