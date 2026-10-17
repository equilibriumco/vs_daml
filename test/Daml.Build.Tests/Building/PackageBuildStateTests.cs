using System;
using System.IO;
using Daml.Build.Core.Building;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Building;

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

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private PackageBuildState Compute(string? source = null) => PackageBuildState.Compute(Manifest, source ?? Source, Dependencies);

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
        var built = PackageBuildState.Compute(Manifest, _root, Dependencies);
        Write(@".daml\package-database\3.6.0\daml-stdlib\DA\List.daml", "module DA.List where\n");
        Write(@"obj\Generated.daml", "module Generated where\n");
        Write(@"bin\Copied.daml", "module Copied where\n");

        PackageBuildState.Compute(Manifest, _root, Dependencies).ShouldBe(built);
    }

    // damlc follows the imports of a single source file, which come from its folder.
    [Fact, Trait("Category", "Unit")]
    public void A_single_source_file_brings_the_files_of_its_folder()
    {
        PackageBuildState.SourceFiles(Path.Combine(Source, "Main.daml"))
            .ShouldBe([Path.Combine(Source, @"Asset\Asset.daml"), Path.Combine(Source, "Main.daml")]);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_source_fingerprint_of_the_last_build_can_be_read_back_without_hashing_anything()
    {
        var built = Built();

        PackageBuildState.RecordedSourceFingerprint(StatePath).ShouldBe(built.SourceFingerprint);
        PackageBuildState.RecordedSourceFingerprint(Path.Combine(_root, "missing.state")).ShouldBeNull();
    }
}
