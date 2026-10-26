using System;
using System.IO;
using Equilibrium.Daml.Build.Core.Codegen;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Codegen;

public sealed class GenerationManifestTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "daml-manifest-" + Guid.NewGuid().ToString("N") + ".manifest");

    public void Dispose() => File.Delete(_path);

    private static GenerationManifest Manifest() => new(
        "0123456789abcdef", "f1n9", @"C:\work\App\App.csproj", DamlReferenceKind.Project, @"C:\work\Contracts\Contracts.msbuildproj", "Contracts",
        @"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar", "da12", "oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:9f04",
        [new GenerationManifestFile(@"Contracts\Asset.cs", "a55e7")]);

    [Fact, Trait("Category", "Unit")]
    public void The_package_and_the_sources_a_generation_came_from_are_read_back()
    {
        var package = new DarProvenance("contracts", "1.2.0", "3.6.0", new string('a', 64), [new string('b', 64), new string('c', 64)]);
        var sources = new SourceProvenance(@"C:\work\Contracts\daml.yaml", "50c");

        (Manifest() with { Package = package, Source = sources }).Write(_path);
        var read = GenerationManifest.Read(_path).ShouldNotBeNull();

        (read.Package, read.Source).ShouldBe((package, sources));
        read.Files.ShouldHaveSingleItem().ShouldBe(new GenerationManifestFile(@"Contracts\Asset.cs", "a55e7"));
    }

    // A committed Source-mode generation travels with the repository: its manifest has to name the
    // project, the reference, the DAR and the sources where the checkout is, not where it was made.
    [Fact, Trait("Category", "Unit")]
    public void A_manifest_moved_with_its_repository_names_the_places_in_the_new_checkout()
    {
        var made = Path.Combine(Path.GetTempPath(), "daml-moved-" + Guid.NewGuid().ToString("N"));
        var moved = Path.Combine(Path.GetTempPath(), "daml-moved-" + Guid.NewGuid().ToString("N"), "deeper");
        try
        {
            var folder = Path.Combine(made, @"App\Generated\Daml\contracts-0123456789abcdef");
            Directory.CreateDirectory(folder);
            var written = new GenerationManifest(
                "0123456789abcdef", "f1n9", Path.Combine(made, @"App\App.csproj"), DamlReferenceKind.Project, Path.Combine(made, @"Contracts\Contracts.msbuildproj"), null,
                Path.Combine(made, @"Contracts\.daml\dist\contracts-1.2.0.dar"), "da12", "oci://x", [new GenerationManifestFile("Asset.cs", "a55e7")])
            {
                Source = new SourceProvenance(Path.Combine(made, @"Contracts\daml.yaml"), "50c"),
            };
            written.Write(Path.Combine(folder, GenerationManifest.FileName));
            File.ReadAllText(Path.Combine(folder, GenerationManifest.FileName)).ShouldNotContain(made, Case.Insensitive);

            var copy = Path.Combine(moved, @"App\Generated\Daml\contracts-0123456789abcdef");
            Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(folder, GenerationManifest.FileName), Path.Combine(copy, GenerationManifest.FileName));
            var read = GenerationManifest.Read(Path.Combine(copy, GenerationManifest.FileName)).ShouldNotBeNull();

            read.Owner.ShouldBe(Path.Combine(moved, @"App\App.csproj"), StringCompareShould.IgnoreCase);
            read.Reference.ShouldBe(Path.Combine(moved, @"Contracts\Contracts.msbuildproj"), StringCompareShould.IgnoreCase);
            read.Dar.ShouldBe(Path.Combine(moved, @"Contracts\.daml\dist\contracts-1.2.0.dar"), StringCompareShould.IgnoreCase);
            read.Source.ShouldNotBeNull().ManifestPath.ShouldBe(Path.Combine(moved, @"Contracts\daml.yaml"), StringCompareShould.IgnoreCase);
        }
        finally
        {
            foreach (var root in new[] { made, Path.GetDirectoryName(moved)! })
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    // The manifest is written into a staging folder and then moved into place, which may be at
    // another depth; the paths are relative to where it ends up.
    [Fact, Trait("Category", "Unit")]
    public void Paths_are_written_relative_to_the_folder_the_manifest_will_live_in()
    {
        var staging = Path.Combine(Path.GetTempPath(), "daml-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var final = @"C:\work\App\Generated\Daml\contracts-0123456789abcdef";
            Manifest().Write(Path.Combine(staging, GenerationManifest.FileName), final);

            File.ReadAllText(Path.Combine(staging, GenerationManifest.FileName)).ShouldContain(@"owner=..\..\..\App.csproj");
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    // Manifests written before paths became relative still read the same.
    [Fact, Trait("Category", "Unit")]
    public void A_manifest_with_full_paths_reads_as_written()
    {
        File.WriteAllText(_path,
            "schema=1\ngenerationId=0123456789abcdef\nfingerprint=f1n9\nowner=C:\\work\\App\\App.csproj\nreferenceKind=project\n" +
            "reference=C:\\work\\Contracts\\Contracts.msbuildproj\nnamespace=\ndar=D:\\vendor\\contracts.dar\ndarSha256=da12\ngenerator=oci://x\nfile=a55e7 Asset.cs\n");

        var read = GenerationManifest.Read(_path).ShouldNotBeNull();

        (read.Owner, read.Reference, read.Dar).ShouldBe((@"C:\work\App\App.csproj", @"C:\work\Contracts\Contracts.msbuildproj", @"D:\vendor\contracts.dar"));
    }

    // A DAR reference has no sources of its own.
    [Fact, Trait("Category", "Unit")]
    public void Without_them_the_manifest_reads_as_before()
    {
        Manifest().Write(_path);
        var read = GenerationManifest.Read(_path).ShouldNotBeNull();

        (read.Package, read.Source).ShouldBe((null, null));
        read.Fingerprint.ShouldBe("f1n9");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\outside.cs")]
    [InlineData(@"..\outside.cs")]
    [InlineData(@"Contracts\..\outside.cs")]
    [InlineData(@"/outside.cs")]
    [InlineData(@"Contracts\Asset.cs:stream")]
    [InlineData(@"Contracts\Asset.cs.")]
    [InlineData(@"Contracts\\Asset.cs")]
    [InlineData("")]
    public void An_invalid_file_path_invalidates_the_ownership_manifest(string file)
    {
        (Manifest() with { Files = [new GenerationManifestFile(file, "abc")] }).Write(_path);
        GenerationManifest.Read(_path).ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Duplicate_paths_in_other_case_or_slash_spelling_are_rejected()
    {
        (Manifest() with
        {
            Files = [
            new GenerationManifestFile(@"Contracts\Asset.cs", "a"),
            new GenerationManifestFile("contracts/asset.cs", "b")]
        }).Write(_path);
        GenerationManifest.Read(_path).ShouldBeNull();
    }
}
