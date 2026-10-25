using System.Linq;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Manifests;

public class DamlPackageBuildTests
{
    private const string Root = @"C:\work\Contracts";
    private const string Manifest = Root + @"\daml.yaml";

    private readonly InMemoryFileSystem _files = new InMemoryFileSystem().AddDirectory(Root + @"\daml");

    private ManifestResult<DamlPackageBuild> Read(string yaml)
    {
        _files.AddFile(Manifest, yaml);
        return DamlPackageBuild.Read(new YamlManifestReader(_files), _files, Manifest);
    }

    private const string Minimal = "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n";

    [Fact, Trait("Category", "Unit")]
    public void Remote_dependencies_and_aliases_are_not_resolved_as_Windows_paths()
    {
        var result = Read(Minimal + "data-dependencies:\n  - oci://registry/contracts:1\n  - '@vendor/contracts:1'\n  - {value: '../lib.dar', main-package-id: abc}\n");
        result.Diagnostics.ShouldBeEmpty();
        result.Value!.DataDependencies.ShouldBe([
            new DamlDependency("oci://registry/contracts:1", null),
            new DamlDependency("@vendor/contracts:1", null),
            new DamlDependency("../lib.dar", @"C:\work\lib.dar"),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_package_reads_with_its_sources_dependencies_and_DAR()
    {
        var result = Read(Minimal +
            "components:\n  - oci://ghcr.io/example/tool:1.0\n" +
            "dependencies:\n  - daml-prim\n  - daml-stdlib\n  - ../lib/finance-1.0.0.dar\n" +
            "data-dependencies:\n  - ../vendor/ledger.dar\n");

        result.Diagnostics.ShouldBeEmpty();
        var package = result.Value.ShouldNotBeNull();
        (package.Name, package.Version, package.SdkVersion).ShouldBe(("contracts", "1.2.0", "3.6.0"));
        package.PackageRoot.ShouldBe(Root);
        package.Components.ShouldBe(["oci://ghcr.io/example/tool:1.0"]);
        package.Source.ShouldBe(new DamlSource(DamlSourceKind.Folder, Root + @"\daml"));
        package.Dependencies.Select(d => (d.Text, d.DarPath)).ShouldBe(
        [
            ("daml-prim", null),
            ("daml-stdlib", null),
            ("../lib/finance-1.0.0.dar", @"C:\work\lib\finance-1.0.0.dar"),
        ]);
        package.DataDependencies.ShouldBe([new DamlDependency("../vendor/ledger.dar", @"C:\work\vendor\ledger.dar")]);
        package.DarPath.ShouldBe(Root + @"\.daml\dist\contracts-1.2.0.dar");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_single_file_can_be_the_source()
    {
        _files.AddFile(Root + @"\daml\Main.daml", "module Main where\n");

        Read(Minimal.Replace("source: daml", "source: daml/Main.daml")).Value!.Source
            .ShouldBe(new DamlSource(DamlSourceKind.File, Root + @"\daml\Main.daml"));
    }

    // damlc stops with "ConfigFieldMissing" on each of these; saying so up front points at the manifest.
    [Fact, Trait("Category", "Unit")]
    public void Name_version_and_source_are_required_and_all_missing_ones_are_reported()
    {
        var result = Read("sdk-version: 3.6.0\n");

        result.Value.ShouldBeNull();
        result.Diagnostics.Select(d => d.Message).ShouldBe(
        [
            "daml.yaml has no 'name'; dpm needs one to build the package.",
            "daml.yaml has no 'version'; dpm needs one to build the package.",
            "daml.yaml has no 'source'; dpm needs one to build the package.",
        ]);
        result.Diagnostics.ShouldAllBe(d => d.Severity == ManifestSeverity.Error && d.Path == Manifest);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_source_that_isnt_there_is_reported()
    {
        var result = Read(Minimal.Replace("source: daml", "source: src"));

        result.Value.ShouldBeNull();
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe($"The source 'src' in daml.yaml doesn't exist ({Root}\\src).");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("  - --output\n  - custom/my.dar\n", Root + @"\custom\my.dar")]
    [InlineData("  - --output=out/x.dar\n", Root + @"\out\x.dar")]
    [InlineData("  - -o\n  - out/y.dar\n", Root + @"\out\y.dar")]
    [InlineData("  - --ghc-option=-Wall\n  - -o\n  - D:/dars/z.dar\n", @"D:\dars\z.dar")]
    public void An_output_in_the_build_options_moves_the_DAR(string options, string darPath) =>
        Read(Minimal + "build-options:\n" + options).Value!.DarPath.ShouldBe(darPath);

    [Theory, Trait("Category", "Unit")]
    [InlineData("  - --output\n", "'--output' in build-options has no file after it.")]
    [InlineData("  - --output=\n", "'--output=' in build-options has no file after it.")]
    [InlineData("  - -o\n  - a.dar\n  - --output=b.dar\n", "build-options name the output more than once; keep one.")]
    [InlineData("  - --output custom/my.dar\n", "build-options entry '--output custom/my.dar' has to be two entries: the option, then the file.")]
    [InlineData("  - -o custom/my.dar\n", "build-options entry '-o custom/my.dar' has to be two entries: the option, then the file.")]
    public void An_output_the_build_couldnt_follow_is_refused(string options, string message)
    {
        var result = Read(Minimal + "build-options:\n" + options);

        result.Value.ShouldBeNull();
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe(message);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_manifest_that_isnt_yaml_keeps_the_readers_diagnostics()
    {
        var result = Read("name: [unclosed\n");
        var readers = new YamlManifestReader(_files).ReadPackage(Manifest).Diagnostics;

        result.Value.ShouldBeNull();
        readers.ShouldContain(d => d.Severity == ManifestSeverity.Error && d.Line != null);
        result.Diagnostics.ShouldBe(readers);
    }

    // The reader skips a field of the wrong shape and reads on, which is right for the editor; a
    // build from a half-read manifest would build something else than dpm alone would.
    [Fact, Trait("Category", "Unit")]
    public void A_field_the_reader_had_to_skip_stops_the_build_with_the_readers_message()
    {
        var result = Read(Minimal + "dependencies:\n  - daml-prim\n  - [nested]\n");

        result.Value.ShouldBeNull();
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe("Every entry of 'dependencies' must be a single value; this one is skipped.");
        result.Diagnostics[0].Line.ShouldBe(7);
    }
}
