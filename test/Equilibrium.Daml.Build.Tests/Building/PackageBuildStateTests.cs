using System;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Building;

public sealed class PackageBuildStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-build-state-" + Guid.NewGuid().ToString("N"));

    public PackageBuildStateTests()
    {
        Write("daml.yaml", "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n");
        Write(@"daml\Main.daml", "module Main where\n");
        Write(@"daml\Asset\Asset.daml", "module Asset.Asset where\n");
        Write(@"vendor\finance-1.0.0.dar", "finance");
        Write(Dar, "dar");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Dar = @".daml\dist\contracts-1.2.0.dar";

    private string Manifest => Path.Combine(_root, "daml.yaml");

    private string Source => Path.Combine(_root, "daml");

    private string StatePath => Path.Combine(_root, @"obj\daml.build.state");

    private string DarPath => Path.Combine(_root, Dar);

    private string[] Dependencies => [Path.Combine(_root, @"vendor\finance-1.0.0.dar")];

    [Fact, Trait("Category", "Unit")]
    public void A_local_DALF_data_dependency_is_tracked_from_manifest_to_build_state()
    {
        File.AppendAllText(Manifest, "data-dependencies: [vendor/producer.dalf]\n");
        Write(@"vendor\producer.dalf", "first DALF");
        var files = new PhysicalFileSystem();
        var package = DamlPackageBuild.Read(new YamlManifestReader(files), files, Manifest).Value!;
        var dependencies = package.DataDependencies.Select(d => d.DarPath!).ToArray();
        dependencies.ShouldBe([Path.Combine(_root, @"vendor\producer.dalf")]);
        PackageBuildState State() => PackageBuildState.Compute(Manifest, Source, dependencies, NoEnvironment, null);
        State().Record(StatePath, DarPath);
        State().IsRecordedIn(StatePath, DarPath).ShouldBeTrue();
        Write(@"vendor\producer.dalf", "changed DALF");
        State().IsRecordedIn(StatePath, DarPath).ShouldBeFalse();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("3.6.0-snapshot.20261020.0", true)]
    [InlineData("3.6.0-rc.1+build.42", true)]
    [InlineData("0.1.0", true)]
    [InlineData("3", false)]
    [InlineData("3.6", false)]
    [InlineData("3.6.0.1", false)]
    [InlineData("3.6.0-01", false)]
    [InlineData("latest", false)]
    public void Only_complete_semantic_SDK_versions_allow_reusing_a_recorded_build(string sdk, bool reusable)
    {
        Write("daml.yaml", $"sdk-version: {sdk}\nname: contracts\nsource: daml\n");
        PackageBuildState State() => PackageBuildState.Compute(Manifest, Source, Dependencies, _ => null, null);
        State().Record(StatePath, DarPath);
        State().IsRecordedIn(StatePath, DarPath).ShouldBe(reusable);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("tools")]
    [InlineData(null)]
    public void Configuration_in_the_child_process_home_invalidates_the_build(string? home)
    {
        var config = home is null ? @"appdata\dpm\dpm-config.yaml" : Path.Combine(home, "dpm-config.yaml");
        PackageBuildState State() => PackageBuildState.Compute(Manifest, Source, Dependencies,
            name => name switch { "DPM_HOME" => home, "APPDATA" => Path.Combine(_root, "appdata"), _ => null }, null);
        Write(config, "edition: open-source\n");
        var before = State();
        before.Record(StatePath, DarPath);
        State().IsRecordedIn(StatePath, DarPath).ShouldBeTrue();
        Write(config, "edition: enterprise\n");
        State().IsRecordedIn(StatePath, DarPath).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void SDK_overrides_and_external_multi_package_content_invalidate_the_DAR()
    {
        var multiRoot = Path.Combine(_root, "external");
        Write(@"external\multi-package.yaml", "sdk-version: 3.5.2\n");
        PackageBuildState State(string sdk) => PackageBuildState.Compute(Manifest, Source, Dependencies,
            name => name switch { "DPM_SDK_VERSION" => sdk, "DPM_MULTI_PACKAGE" => multiRoot, _ => null }, null);
        var before = State("3.5.2");
        State("3.6.0").Fingerprint.ShouldNotBe(before.Fingerprint);
        Write(@"external\multi-package.yaml", "sdk-version: 3.6.0\n");
        State("3.5.2").Fingerprint.ShouldNotBe(before.Fingerprint);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("3.6.0", "3.6.0.yaml")]
    [InlineData("3.6.0+build.42", "3.6.0+other-build.yaml")]
    public void An_enterprise_SDK_manifest_change_invalidates_the_DAR_without_an_edition_environment_override(string sdk, string manifest)
    {
        var installed = Path.Combine(@"tools\cache\sdk\enterprise", manifest);
        Write(@"tools\dpm-config.yaml", "edition: enterprise\n");
        Write(installed, "components: [damlc:3.6.1]\n");
        PackageBuildState State() => PackageBuildState.Compute(Manifest, Source, Dependencies,
            name => name switch { "DPM_HOME" => Path.Combine(_root, "tools"), "DPM_SDK_VERSION" => sdk, _ => null }, null);
        var before = State();
        Write(installed, "components: [damlc:3.6.2]\n");
        State().Fingerprint.ShouldNotBe(before.Fingerprint);
    }

    [Fact, Trait("Category", "Unit")]
    public void Expanded_manifest_values_change_the_fingerprint()
    {
        Write("daml.yaml", "sdk-version: 3.6.0\nname: $NAME\nsource: daml\n");
        PackageBuildState State(string value) => PackageBuildState.Compute(Manifest, Source, Dependencies, name => name == "NAME" ? value : null, null);
        State("first").Fingerprint.ShouldNotBe(State("second").Fingerprint);
    }

    [Fact, Trait("Category", "Unit")]
    public void Unpinned_or_overridden_components_are_resolved_again_even_after_recording()
    {
        Write("daml.yaml", "name: contracts\nsource: daml\n");
        var unpinned = Compute();
        unpinned.Record(StatePath, DarPath);
        unpinned.IsRecordedIn(StatePath, DarPath).ShouldBeFalse();
        Write("daml.yaml", "sdk-version: 3.6.0\ncomponents:\n  - {name: damlc, path: ../compiler}\n");
        Compute().CanReuse.ShouldBeFalse();
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // No DPM_* variables and no dpm on PATH, whatever this machine has: the state depends on both.
    private static string? NoEnvironment(string name) => null;

    private PackageBuildState Compute(string? source = null) => PackageBuildState.Compute(Manifest, source ?? Source, Dependencies, NoEnvironment, null);

    // The DAR is current once a build recorded the state it was built from.
    private PackageBuildState Built()
    {
        var state = Compute();
        state.Record(StatePath, DarPath);
        return state;
    }

    [Fact, Trait("Category", "Unit")]
    public void The_same_inputs_give_the_same_state_and_a_recorded_build_is_current()
    {
        var built = Built();

        Compute().ShouldBe(built);
        Compute().IsRecordedIn(StatePath, DarPath).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public void Nothing_recorded_yet_isnt_current() => Compute().IsRecordedIn(StatePath, DarPath).ShouldBeFalse();

    // What a build records once dpm made the DAR: the state it computed before, if the inputs are
    // still that state.
    [Fact, Trait("Category", "Unit")]
    public void A_build_from_inputs_that_stayed_put_is_recorded()
    {
        var before = Compute();

        before.RecordIfUnchanged(Manifest, Source, Dependencies, StatePath, DarPath, environment: NoEnvironment).ShouldBeTrue();

        Compute().IsRecordedIn(StatePath, DarPath).ShouldBeTrue();
    }

    // The DAR may hold the old sources or the new ones; only a fresh build can say which.
    [Fact, Trait("Category", "Unit")]
    public void Sources_changed_while_dpm_built_are_not_recorded_as_built()
    {
        var before = Compute();
        Write(@"daml\Main.daml", "module Main where\nx = 1\n");

        before.RecordIfUnchanged(Manifest, Source, Dependencies, StatePath, DarPath, environment: NoEnvironment).ShouldBeFalse();

        File.Exists(StatePath).ShouldBeFalse();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("edit")]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("rename")]
    public void Any_change_to_the_set_of_sources_or_their_content_makes_it_stale(string change)
    {
        var built = Built();
        var asset = Path.Combine(Source, @"Asset\Asset.daml");
        switch (change)
        {
            case "edit":
                File.WriteAllText(asset, "module Asset.Asset where\nx = 1\n");
                break;
            case "add":
                Write(@"daml\Asset\Holding.daml", "module Asset.Holding where\n");
                break;
            case "delete":
                File.Delete(asset);
                break;
            default:
                File.Move(asset, Path.Combine(Source, @"Asset\Renamed.daml"));
                break;
        }

        var now = Compute();

        now.SourceFingerprint.ShouldNotBe(built.SourceFingerprint);
        now.IsRecordedIn(StatePath, DarPath).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_changed_manifest_makes_it_stale_but_keeps_the_source_fingerprint()
    {
        var built = Built();
        File.AppendAllText(Manifest, "build-options:\n  - --target=2.3\n");

        var now = Compute();

        now.SourceFingerprint.ShouldBe(built.SourceFingerprint);
        now.IsRecordedIn(StatePath, DarPath).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Changing_the_multi_package_configuration_invalidates_the_DAR()
    {
        Write("multi-package.yaml", "sdk-version: 3.5.2\npackages: [.]");
        var built = Built();

        Write("multi-package.yaml", "sdk-version: 3.6.0\npackages: [.]");

        var now = Compute();
        now.SourceFingerprint.ShouldBe(built.SourceFingerprint);
        now.IsRecordedIn(StatePath, DarPath).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Adding_or_removing_a_nearer_multi_package_changes_the_build_context()
    {
        Write("multi-package.yaml", "sdk-version: 3.5.2\npackages: [nested]");
        Write(@"nested\daml.yaml", "name: nested\nversion: 1.0.0\nsource: ../daml\n");
        var manifest = Path.Combine(_root, @"nested\daml.yaml");
        var before = PackageBuildState.Compute(manifest, Source, Dependencies, NoEnvironment, null);

        Write(@"nested\multi-package.yaml", "sdk-version: 3.6.0\npackages: [.]");
        PackageBuildState.Compute(manifest, Source, Dependencies, NoEnvironment, null).Fingerprint.ShouldNotBe(before.Fingerprint);

        File.Delete(Path.Combine(_root, @"nested\multi-package.yaml"));
        PackageBuildState.Compute(manifest, Source, Dependencies, NoEnvironment, null).ShouldBe(before);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public void A_changed_or_missing_dependency_DAR_makes_it_stale(bool delete)
    {
        Built();
        if (delete)
        {
            File.Delete(Dependencies[0]);
        }
        else
        {
            File.WriteAllText(Dependencies[0], "finance 1.0.1");
        }

        Compute().IsRecordedIn(StatePath, DarPath).ShouldBeFalse();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public void A_missing_or_replaced_DAR_isnt_current(bool delete)
    {
        Built();
        if (delete)
        {
            File.Delete(DarPath);
        }
        else
        {
            File.WriteAllText(DarPath, "a DAR from somewhere else");
        }

        Compute().IsRecordedIn(StatePath, DarPath).ShouldBeFalse();
    }

    // With the package folder as its source, damlc's own output and the build folders are no sources.
    [Fact, Trait("Category", "Unit")]
    public void Build_output_under_the_source_folder_is_no_source()
    {
        PackageBuildState.SourceFiles(_root).ShouldBe([Path.Combine(_root, @"daml\Asset\Asset.daml"), Path.Combine(_root, @"daml\Main.daml")]);
        var built = PackageBuildState.Compute(Manifest, _root, Dependencies, NoEnvironment, null);
        Write(@".daml\package-database\3.6.0\daml-stdlib\DA\List.daml", "module DA.List where\n");
        Write(@"obj\Generated.daml", "module Generated where\n");
        Write(@"bin\Copied.daml", "module Copied where\n");

        PackageBuildState.Compute(Manifest, _root, Dependencies, NoEnvironment, null).ShouldBe(built);
    }

    // damlc follows the imports of a single source file, which come from its folder.
    [Fact, Trait("Category", "Unit")]
    public void A_single_source_file_brings_the_files_of_its_folder()
    {
        PackageBuildState.SourceFiles(Path.Combine(Source, "Main.daml"))
            .ShouldBe([Path.Combine(Source, @"Asset\Asset.daml"), Path.Combine(Source, "Main.daml")]);
    }

    // What the IDE compares with a generation's record, without the tool and SDK inputs of a build.
    [Theory, Trait("Category", "Unit")]
    [InlineData("daml")]
    [InlineData(@"daml\Main.daml")]
    public void The_source_fingerprint_alone_is_the_one_the_build_state_has(string source)
    {
        var sourcePath = Path.Combine(_root, source);

        PackageBuildState.SourceFingerprintOf(sourcePath)
            .ShouldBe(PackageBuildState.Compute(Manifest, sourcePath, Dependencies, NoEnvironment, null).SourceFingerprint);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_source_fingerprint_of_the_last_build_can_be_read_back_without_hashing_anything()
    {
        var built = Built();

        PackageBuildState.RecordedSourceFingerprint(StatePath, DarPath).ShouldBe(built.SourceFingerprint);
        PackageBuildState.RecordedSourceFingerprint(Path.Combine(_root, "missing.state"), DarPath).ShouldBeNull();
    }

    // A DAR put there by something else wasn't built from the recorded sources.
    [Fact, Trait("Category", "Unit")]
    public void A_replaced_DAR_has_no_recorded_sources()
    {
        Built();
        File.WriteAllText(DarPath, "a DAR from somewhere else");

        PackageBuildState.RecordedSourceFingerprint(StatePath, DarPath).ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Removing_the_outputs_takes_the_DAR_and_the_record_and_nothing_else()
    {
        Built();

        PackageBuildState.RemoveOutputs(StatePath, DarPath);
        PackageBuildState.RemoveOutputs(StatePath, DarPath);

        (File.Exists(DarPath), File.Exists(StatePath), File.Exists(Manifest), Directory.Exists(Source)).ShouldBe((false, false, true, true));
    }
}
