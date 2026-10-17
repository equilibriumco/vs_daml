using System;
using System.IO;
using System.Linq;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Tests.FileSystem;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public sealed class GenerationPublisherTests : IDisposable
{
    private const string Id = "0123456789abcdef";
    private const string Folder = "Contracts-" + Id;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-publish-" + Guid.NewGuid().ToString("N"), "Daml");

    public void Dispose() => DeepPath.Delete(Path.GetDirectoryName(_root)!);

    private string Active => Path.Combine(_root, Folder);

    private string Work => Path.Combine(_root, GenerationPublisher.WorkFolderName);

    private string Backup => Path.Combine(Work, Id + ".old");

    private string Journal => Path.Combine(Work, Id + ".journal");

    private string LeftStaging => Path.Combine(Work, Id + ".new");

    private static void Fill(string folder, string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Asset.cs"), content);
    }

    private static string Content(string folder) => File.ReadAllText(Path.Combine(folder, "Asset.cs"));

    private static string Staged(GenerationPublisher publisher, string content)
    {
        var staging = publisher.NewStaging(Folder);
        Fill(staging, content);
        return staging;
    }

    // Moves and deletes as the publisher does them, failing the one a test picks.
    private sealed class FailingSteps(Func<string, string, bool> failsWhen) : IPublishSteps
    {
        public void Move(string from, string to)
        {
            if (failsWhen("move", from + " -> " + to))
            {
                throw new IOException($"injected: can't move {from}");
            }
            Directory.Move(from, to);
        }

        public void Delete(string folder)
        {
            if (failsWhen("delete", folder))
            {
                throw new IOException($"injected: can't delete {folder}");
            }
            Directory.Delete(folder, recursive: true);
        }

        public void Write(string file, string text)
        {
            if (failsWhen("write", file))
            {
                throw new IOException($"injected: can't write {file}");
            }
            File.WriteAllText(file, text);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void A_journal_that_cant_be_written_stops_the_publish_before_anything_moves()
    {
        Fill(Active, "old");
        var publisher = new GenerationPublisher(_root, new FailingSteps((step, what) => step == "write" && what == Journal));
        var staging = Staged(publisher, "new");

        Should.Throw<GenerationPublishException>(() => publisher.Publish(Folder, staging)).Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);

        Content(Active).ShouldBe("old");
        Content(staging).ShouldBe("new");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_first_publish_puts_the_staged_generation_in_place_and_leaves_nothing_behind()
    {
        var publisher = new GenerationPublisher(_root);
        var staging = Staged(publisher, "new");

        publisher.Publish(Folder, staging);

        Content(Active).ShouldBe("new");
        Directory.Exists(staging).ShouldBeFalse();
        (Directory.Exists(Backup), File.Exists(Journal)).ShouldBe((false, false));
        // Inside a Source folder the work folder is in the project's tree, but never in git.
        File.ReadAllText(Path.Combine(Work, ".gitignore")).Trim().ShouldBe("*");
    }

    [Fact, Trait("Category", "Unit")]
    public void An_old_generation_with_files_past_the_path_limit_is_replaced_and_its_backup_removed()
    {
        DeepPath.Write(Active, DeepPath.Relative, "old");
        var publisher = new GenerationPublisher(_root);

        publisher.Publish(Folder, Staged(publisher, "new"));

        Content(Active).ShouldBe("new");
        LongPaths.FileExists(Path.Combine(Active, DeepPath.Relative)).ShouldBeFalse();
        Directory.Exists(Backup).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_new_generation_replaces_the_old_one()
    {
        Fill(Active, "old");
        var publisher = new GenerationPublisher(_root);

        publisher.Publish(Folder, Staged(publisher, "new"));

        Content(Active).ShouldBe("new");
        Directory.Exists(Backup).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Failing_to_move_the_old_generation_aside_leaves_it_in_place()
    {
        Fill(Active, "old");
        var publisher = new GenerationPublisher(_root, new FailingSteps((step, what) => step == "move" && what.StartsWith(Active, StringComparison.Ordinal)));
        var staging = Staged(publisher, "new");

        var failure = Should.Throw<GenerationPublishException>(() => publisher.Publish(Folder, staging));

        failure.Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
        Content(Active).ShouldBe("old");
        File.Exists(Journal).ShouldBeFalse();
    }

    // Not left for the next build to recover: the old generation is back before the build goes on.
    [Fact, Trait("Category", "Unit")]
    public void Failing_to_move_the_new_generation_in_puts_the_old_one_back_at_once()
    {
        Fill(Active, "old");
        var publisher = new GenerationPublisher(_root, new FailingSteps((step, what) => step == "move" && what.EndsWith(" -> " + Active, StringComparison.Ordinal) && !what.StartsWith(Backup, StringComparison.Ordinal)));
        var staging = Staged(publisher, "new");

        var failure = Should.Throw<GenerationPublishException>(() => publisher.Publish(Folder, staging));

        failure.Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
        failure.Message.ShouldContain("the previous generation is back in place");
        Content(Active).ShouldBe("old");
        (Directory.Exists(Backup), File.Exists(Journal)).ShouldBe((false, false));
    }

    [Fact, Trait("Category", "Unit")]
    public void When_the_old_generation_cant_be_put_back_it_is_kept_for_the_next_build()
    {
        Fill(Active, "old");
        var publisher = new GenerationPublisher(_root, new FailingSteps((step, what) => step == "move" && what.EndsWith(" -> " + Active, StringComparison.Ordinal)));
        var staging = Staged(publisher, "new");

        var failure = Should.Throw<GenerationPublishException>(() => publisher.Publish(Folder, staging));

        failure.Code.ShouldBe(DamlDiagnosticCodes.RollbackFailed);
        failure.Message.ShouldContain(Backup);
        (Directory.Exists(Active), Content(Backup), File.Exists(Journal)).ShouldBe((false, "old", true));

        new GenerationPublisher(_root).Recover(Folder).ShouldBe(RecoveryOutcome.RolledBack);
        Content(Active).ShouldBe("old");
        (Directory.Exists(Backup), File.Exists(Journal)).ShouldBe((false, false));
    }

    // Past the commit, a backup that won't go is left for later; it never reaches Compile.
    [Fact, Trait("Category", "Unit")]
    public void A_backup_that_wont_go_after_the_commit_doesnt_fail_the_publish()
    {
        Fill(Active, "old");
        var publisher = new GenerationPublisher(_root, new FailingSteps((step, what) => step == "delete" && what == Backup));

        publisher.Publish(Folder, Staged(publisher, "new"));

        Content(Active).ShouldBe("new");
        File.Exists(Journal).ShouldBeFalse();
        new GenerationPublisher(_root).Recover(Folder).ShouldBe(RecoveryOutcome.Cleaned);
        Directory.Exists(Backup).ShouldBeFalse();
    }

    // What a build killed between the steps leaves, and what the next build makes of it.
    [Fact, Trait("Category", "Unit")]
    public void Killed_before_the_old_generation_moved_the_next_build_drops_the_staging()
    {
        Fill(Active, "old");
        var staging = LeftStaging;
        Fill(staging, "new");
        File.WriteAllText(Journal, $"staging={Path.GetFileName(staging)}\nphase=staged\n");

        new GenerationPublisher(_root).Recover(Folder).ShouldBe(RecoveryOutcome.Cleaned);

        Content(Active).ShouldBe("old");
        (Directory.Exists(staging), File.Exists(Journal)).ShouldBe((false, false));
    }

    [Fact, Trait("Category", "Unit")]
    public void Killed_with_the_old_generation_aside_the_next_build_puts_it_back()
    {
        Fill(Backup, "old");
        var staging = LeftStaging;
        Fill(staging, "new");
        File.WriteAllText(Journal, $"staging={Path.GetFileName(staging)}\nphase=old-moved\n");

        new GenerationPublisher(_root).Recover(Folder).ShouldBe(RecoveryOutcome.RolledBack);

        Content(Active).ShouldBe("old");
        (Directory.Exists(staging), Directory.Exists(Backup), File.Exists(Journal)).ShouldBe((false, false, false));
    }

    [Fact, Trait("Category", "Unit")]
    public void Killed_after_the_new_generation_moved_in_the_next_build_finishes_the_publish()
    {
        Fill(Active, "new");
        Fill(Backup, "old");
        File.WriteAllText(Journal, $"staging={Id}.new\nphase=new-moved\n");

        new GenerationPublisher(_root).Recover(Folder).ShouldBe(RecoveryOutcome.Completed);

        Content(Active).ShouldBe("new");
        (Directory.Exists(Backup), File.Exists(Journal)).ShouldBe((false, false));
    }

    // A generator killed mid-run leaves its staging and run folder behind and no journal.
    [Fact, Trait("Category", "Unit")]
    public void Stagings_left_by_a_killed_generator_are_removed_and_other_generations_are_not_touched()
    {
        Fill(LeftStaging, "half");
        Fill(GenerationPublisher.RunFolderOf(LeftStaging), "temp");
        Fill(Path.Combine(Work, "fedcba9876543210.new"), "other");

        new GenerationPublisher(_root).Recover(Folder).ShouldBe(RecoveryOutcome.Cleaned);

        Directory.GetDirectories(Work).Select(Path.GetFileName).ShouldBe(["fedcba9876543210.new"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_staging_left_by_an_earlier_run_must_be_recovered_before_a_new_one()
    {
        Fill(LeftStaging, "half");
        var publisher = new GenerationPublisher(_root);

        Should.Throw<InvalidOperationException>(() => publisher.NewStaging(Folder));

        publisher.Recover(Folder);
        Directory.GetFiles(publisher.NewStaging(Folder)).ShouldBeEmpty();
    }

    // The generator's file names are long; a staged file mustn't hit Windows' path limit long
    // before the same file published would.
    [Fact, Trait("Category", "Unit")]
    public void A_staged_path_is_hardly_longer_than_the_published_one()
    {
        var staging = new GenerationPublisher(_root).NewStaging(Folder);

        (Path.Combine(staging, "Asset.cs").Length - Path.Combine(Active, "Asset.cs").Length).ShouldBeLessThanOrEqualTo(5);
        (GenerationPublisher.RunFolderOf(staging).Length - Active.Length).ShouldBeLessThanOrEqualTo(5);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_first_publish_that_fails_publishes_nothing_and_keeps_the_staging_for_the_caller()
    {
        var publisher = new GenerationPublisher(_root, new FailingSteps((step, what) => step == "move"));
        var staging = Staged(publisher, "new");

        Should.Throw<GenerationPublishException>(() => publisher.Publish(Folder, staging)).Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);

        (Directory.Exists(Active), File.Exists(Journal), Directory.Exists(staging)).ShouldBe((false, false, true));
        publisher.Discard(staging);
        Directory.Exists(staging).ShouldBeFalse();
    }

    // Killed right after writing the journal of a first publish: nothing to put back, nothing to finish.
    [Fact, Trait("Category", "Unit")]
    public void A_journal_with_nothing_behind_it_is_just_removed()
    {
        Directory.CreateDirectory(Work);
        File.WriteAllText(Journal, $"staging={Id}.new\nphase=staged\n");

        new GenerationPublisher(_root).Recover(Folder).ShouldBe(RecoveryOutcome.Cleaned);

        (Directory.Exists(Active), File.Exists(Journal)).ShouldBe((false, false));
    }

    // Denied access is what Windows says about a read-only file; it must not escape the rollback.
    [Fact, Trait("Category", "Unit")]
    public void A_read_only_file_in_the_way_fails_a_step_as_an_IO_error()
    {
        var folder = Path.Combine(_root, "read-only");
        Fill(folder, "kept");
        var file = Path.Combine(folder, "Asset.cs");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            Should.Throw<IOException>(() => FileSystemPublishSteps.Instance.Delete(folder)).InnerException.ShouldBeOfType<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private const string Owner = @"C:\work\App\App.csproj";

    // A published generation of Owner: its files, written last its manifest.
    private static void Published(string folder, string owner = Owner, params string[] files)
    {
        var listed = files.Length == 0 ? [@"Contracts\Asset\Asset.cs", @"Contracts\Asset\Asset.Give.cs"] : files;
        foreach (var file in listed.Where(f => !f.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(f)))
        {
            DeepPath.Write(folder, file, "generated");
        }
        new GenerationManifest(Path.GetFileName(folder).Substring(Path.GetFileName(folder).Length - 16), "f1n9", owner, DamlReferenceKind.Project,
            @"C:\work\Contracts\Contracts.msbuildproj", "Contracts", @"C:\work\contracts.dar", "da12", "oci://pin",
            listed.Select(f => new GenerationManifestFile(f, "0")).ToList()).Write(Path.Combine(folder, GenerationManifest.FileName));
    }

    [Fact, Trait("Category", "Unit")]
    public void Removing_a_generation_takes_its_files_its_folders_and_its_work_entries_and_nothing_else()
    {
        Published(Active);
        Published(Path.Combine(_root, "Finance-fedcba9876543210"));
        Fill(LeftStaging, "half");
        Fill(GenerationPublisher.RunFolderOf(LeftStaging), "temp");
        Fill(Path.Combine(Work, "fedcba9876543210.new"), "other");

        var removal = new GenerationPublisher(_root).Remove(Folder, Owner);

        (removal.Owned, removal.KeptFiles.Count).ShouldBe((true, 0));
        Directory.Exists(Active).ShouldBeFalse();
        Directory.GetFileSystemEntries(Work).Select(Path.GetFileName).ShouldBe(["fedcba9876543210.new"]);
        GenerationManifest.Read(Path.Combine(_root, @"Finance-fedcba9876543210", GenerationManifest.FileName)).ShouldNotBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Files_the_generator_didnt_write_are_kept_with_their_folders()
    {
        Published(Active);
        File.WriteAllText(Path.Combine(Active, @"Contracts\Asset\Mine.cs"), "class Mine { }");

        var removal = new GenerationPublisher(_root).Remove(Folder, Owner);

        removal.KeptFiles.ShouldBe([@"Contracts\Asset\Mine.cs"]);
        File.Exists(Path.Combine(Active, @"Contracts\Asset\Mine.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(Active, @"Contracts\Asset\Asset.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(Active, GenerationManifest.FileName)).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Another_projects_generation_is_left_alone()
    {
        Published(Active, owner: @"C:\work\Other\Other.csproj");

        new GenerationPublisher(_root).Remove(Folder, Owner).Owned.ShouldBeFalse();

        File.Exists(Path.Combine(Active, @"Contracts\Asset\Asset.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(Active, GenerationManifest.FileName)).ShouldBeTrue();
    }

    // Only files inside the folder can be the generation's, whatever its manifest says.
    [Fact, Trait("Category", "Unit")]
    public void A_manifest_entry_pointing_outside_the_folder_deletes_nothing_there()
    {
        var outside = Path.Combine(_root, "outside.cs");
        Directory.CreateDirectory(_root);
        File.WriteAllText(outside, "class Outside { }");
        Published(Active, Owner, @"Contracts\Asset.cs", @"..\outside.cs", @"C:\elsewhere\file.cs");

        new GenerationPublisher(_root).Remove(Folder, Owner);

        File.Exists(outside).ShouldBeTrue();
        Directory.Exists(Active).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_a_manifest_only_empty_folders_go()
    {
        Fill(Active, "whose?");
        Directory.CreateDirectory(Path.Combine(_root, "Empty-0000000000000000", "Sub"));
        var publisher = new GenerationPublisher(_root);

        publisher.Remove(Folder, Owner).KeptFiles.ShouldBe(["Asset.cs"]);
        publisher.Remove("Empty-0000000000000000", Owner).KeptFiles.ShouldBeEmpty();

        Content(Active).ShouldBe("whose?");
        Directory.Exists(Path.Combine(_root, "Empty-0000000000000000")).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_generation_with_files_past_the_path_limit_is_removed()
    {
        Published(Active, Owner, DeepPath.Relative);

        new GenerationPublisher(_root).Remove(Folder, Owner).KeptFiles.ShouldBeEmpty();

        Directory.Exists(Active).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Generation_folders_and_work_entries_are_told_apart_from_anything_else()
    {
        Published(Active);
        Directory.CreateDirectory(Path.Combine(_root, "Notes"));
        Fill(Path.Combine(Work, "fedcba9876543210.old"), "old");
        File.WriteAllText(Path.Combine(Work, "1111111111111111.journal"), "staging=1111111111111111.new\n");
        var publisher = new GenerationPublisher(_root);

        publisher.FolderNames().ShouldBe([Folder]);
        publisher.WorkIds().ShouldBe(["1111111111111111", "fedcba9876543210"], ignoreOrder: true);

        publisher.RemoveWork("fedcba9876543210");
        publisher.RemoveWork("1111111111111111");
        publisher.Remove(Folder, Owner);
        publisher.RemoveIfEmpty();

        Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).ShouldBe(["Notes"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_empty_root_goes_too()
    {
        Published(Active);
        var publisher = new GenerationPublisher(_root);

        publisher.Remove(Folder, Owner);
        publisher.RemoveIfEmpty();

        Directory.Exists(_root).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Nothing_to_recover_changes_nothing()
    {
        Fill(Active, "current");

        new GenerationPublisher(_root).Recover(Folder).ShouldBe(RecoveryOutcome.Nothing);

        Content(Active).ShouldBe("current");
    }
}
