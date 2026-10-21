using System;
using System.IO;
using System.Linq;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.FileSystem;
using Daml.Build.Tests.FileSystem;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public sealed class GenerationStateTests : IDisposable
{
    private const string Project = @"C:\work\App\App.csproj";
    private const string Fingerprint = "f00d";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "daml-generation-" + Guid.NewGuid().ToString("N"), "Contracts-0123456789abcdef");

    public void Dispose() => DeepPath.Delete(Path.GetDirectoryName(_folder)!);

    private static GenerationManifest Manifest(params (string Path, string Content)[] files) => new(
        GenerationId: "0123456789abcdef",
        Fingerprint: Fingerprint,
        Owner: Project,
        ReferenceKind: DamlReferenceKind.Project,
        Reference: @"C:\work\Contracts\Contracts.msbuildproj",
        Namespace: "Contracts",
        Dar: @"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar",
        DarSha256: "da12",
        Generator: "oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:9f04",
        Files: files.Select(f => new GenerationManifestFile(f.Path, Hashing.Sha256Text(f.Content))).ToList());

    // A generation as a publish leaves it: its files and, written last, its manifest.
    private void Published(GenerationManifest manifest, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        manifest.Write(Path.Combine(_folder, GenerationManifest.FileName));
    }

    private static readonly (string, string)[] Asset = [(@"S09\Asset\Asset.cs", "partial record Asset"), (@"S09\Asset\Asset.Give.cs", "partial record Asset { }")];

    private GenerationState Evaluate(string fingerprint = Fingerprint, string owner = Project) => GenerationState.Evaluate(_folder, fingerprint, owner);

    [Fact, Trait("Category", "Unit")]
    public void An_intact_generation_with_the_same_fingerprint_is_current_with_its_files()
    {
        Published(Manifest(Asset), Asset);

        var state = Evaluate();

        state.Status.ShouldBe(GenerationStatus.Current);
        state.Files.ShouldBe([Path.Combine(_folder, @"S09\Asset\Asset.cs"), Path.Combine(_folder, @"S09\Asset\Asset.Give.cs")]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_generation_with_files_past_the_path_limit_is_judged_like_any_other()
    {
        var path = DeepPath.Write(_folder, DeepPath.Relative, "class Registration { }");
        Manifest((DeepPath.Relative, "class Registration { }")).Write(Path.Combine(_folder, GenerationManifest.FileName));

        Evaluate().Files.ShouldBe([path]);

        DeepPath.Write(_folder, DeepPath.Relative, "class Changed { }");
        Evaluate().Status.ShouldBe(GenerationStatus.Stale);
    }

    [Fact, Trait("Category", "Unit")]
    public void No_folder_is_no_generation_yet() =>
        (Evaluate().Status, Evaluate().Reason).ShouldBe((GenerationStatus.Stale, "There is no generation yet."));

    [Fact, Trait("Category", "Unit")]
    public void Other_inputs_make_it_stale()
    {
        Published(Manifest(Asset), Asset);

        (Evaluate(fingerprint: "beef").Status, Evaluate(fingerprint: "beef").Reason).ShouldBe((GenerationStatus.Stale, "Its inputs changed."));
    }

    // Before a build holds the output lock, a clean of another build can remove the folder between
    // the existence check and the read of its files. A folder that is a junction to a removed target
    // fails the same way, and does so every time.
    [Fact, Trait("Category", "Unit")]
    public void A_folder_removed_while_it_is_read_counts_as_stale()
    {
        var target = Path.Combine(Path.GetDirectoryName(_folder)!, "target");
        Directory.CreateDirectory(target);
        Junction(_folder, target);
        try
        {
            Directory.Delete(target);
            Directory.Exists(_folder).ShouldBeTrue();

            var state = Evaluate();

            state.Status.ShouldBe(GenerationStatus.Stale);
            state.Reason.ShouldBe("It was removed while this build looked at it.");
        }
        finally
        {
            // A junction goes with a plain delete; the recursive one of Dispose can't look inside it.
            Directory.Delete(_folder);
        }
    }

    private static void Junction(string link, string target)
    {
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        mklink.ExitCode.ShouldBe(0, mklink.StandardOutput.ReadToEnd());
    }

    [Fact, Trait("Category", "Unit")]
    public void A_deleted_file_makes_it_stale()
    {
        Published(Manifest(Asset), Asset);
        File.Delete(Path.Combine(_folder, @"S09\Asset\Asset.Give.cs"));

        Evaluate().Reason.ShouldBe(@"S09\Asset\Asset.Give.cs was deleted.");
    }

    // The generator owns what it wrote: an edit is overwritten by the next generation.
    [Fact, Trait("Category", "Unit")]
    public void An_edited_file_makes_it_stale()
    {
        Published(Manifest(Asset), Asset);
        File.AppendAllText(Path.Combine(_folder, @"S09\Asset\Asset.cs"), " // mine");

        (Evaluate().Status, Evaluate().Reason).ShouldBe((GenerationStatus.Stale, @"S09\Asset\Asset.cs was changed since it was generated."));
    }

    // A file the generator didn't write is never deleted with the generation; the build stops instead.
    [Fact, Trait("Category", "Unit")]
    public void A_file_the_generator_didnt_write_is_a_conflict()
    {
        Published(Manifest(Asset), Asset);
        File.WriteAllText(Path.Combine(_folder, @"S09\Notes.cs"), "// mine");

        var state = Evaluate();

        state.Status.ShouldBe(GenerationStatus.Conflict);
        state.ForeignFiles.ShouldBe([@"S09\Notes.cs"]);
        state.Reason.ShouldBe(
            $@"{_folder} has files the generator didn't write: S09\Notes.cs. The build never deletes them; move them out, or give DamlGeneratedDir a folder of its own.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_folder_of_files_without_a_manifest_is_a_conflict()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "Handwritten.cs"), "class X { }");

        var state = Evaluate();

        (state.Status, state.ForeignFiles.Single()).ShouldBe((GenerationStatus.Conflict, "Handwritten.cs"));
    }

    [Fact, Trait("Category", "Unit")]
    public void An_empty_folder_is_no_generation_yet()
    {
        Directory.CreateDirectory(_folder);

        Evaluate().Status.ShouldBe(GenerationStatus.Stale);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_generation_of_another_project_is_a_conflict()
    {
        Published(Manifest(Asset), Asset);

        var state = Evaluate(owner: @"C:\work\Other\Other.csproj");

        state.Status.ShouldBe(GenerationStatus.Conflict);
        state.Reason.ShouldBe($@"{_folder} holds bindings generated for C:\work\App\App.csproj, not for C:\work\Other\Other.csproj. Give each project a DamlGeneratedDir of its own.");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("schema=9\n")]
    [InlineData("schema=1\nowner=another-project\n")]
    public void An_unreadable_manifest_cannot_establish_ownership(string contents)
    {
        Published(Manifest(Asset), Asset);
        var path = Path.Combine(_folder, GenerationManifest.FileName);
        File.WriteAllText(path, contents);
        File.WriteAllText(Path.Combine(_folder, "Handwritten.cs"), "class Mine { }");

        var state = Evaluate();

        state.Status.ShouldBe(GenerationStatus.Conflict);
        state.Reason.ShouldNotBeNull().ShouldContain("ownership");
        state.ForeignFiles.ShouldContain("Handwritten.cs");
    }

    [Fact, Trait("Category", "Unit")]
    public void The_manifest_reads_back_what_was_written()
    {
        var manifest = Manifest((@"Zażółć\Gęś ka.cs", "x"), (@"A\B.cs", "y"));
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, GenerationManifest.FileName);

        manifest.Write(path);

        var read = GenerationManifest.Read(path).ShouldNotBeNull();
        read.ShouldBe(manifest with { Files = read.Files });
        read.Files.ShouldBe(manifest.Files);
        File.ReadAllText(path).ShouldStartWith("schema=1\n");
    }
}
