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
