using System;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Bindings;

public sealed class DamlSourcesTests : IDisposable
{
    private const string Package = "bd2aa85e017dad908dbd9c8103929df3edaa7b0961b675d3ef216644565f9cb1";

    private static readonly DamlSymbolId Asset = new(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-sources-" + Guid.NewGuid().ToString("N"));
    private readonly DamlSources _sources = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // A Daml package, and the fingerprint of its sources a build would record now.
    private SourceProvenance Package_(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, @"daml\Kinds"));
        File.WriteAllText(Path.Combine(folder, "daml.yaml"), $"sdk-version: 3.6.0\nname: {name.ToLowerInvariant()}\nversion: 1.0.0\nsource: daml\n");
        File.WriteAllText(Path.Combine(folder, @"daml\Kinds\Asset.daml"), "module Kinds.Asset where\n");
        var manifest = Path.Combine(folder, "daml.yaml");
        return new SourceProvenance(manifest, PackageBuildState.Compute(manifest, Path.Combine(folder, "daml"), []).SourceFingerprint);
    }

    private static GenerationProvenance Generation(string project, SourceProvenance? source) =>
        GenerationProvenance.From(Path.Combine(@"C:\work", project, @"obj\Debug\net10.0\Daml\Contracts-9de80d6cfe50bada"), GenerationProvenanceTests.Manifest() with { Source = source })!;

    private static BindingLocation At(GenerationProvenance generation, string project) =>
        new(Asset, project, null, generation, "T:Fixture.Kinds.Asset.Asset", Path.Combine(generation.Folder, @"Fixture\Kinds\Asset\Asset.cs"), 100, 5);

    [Fact, Trait("Category", "Unit")]
    public void Sources_the_DAR_was_built_from_and_still_are_are_current() =>
        _sources.StatusOf(Generation("App", Package_("Contracts"))).ShouldBe(SourceStatus.Current);

    // Daml edited without building: the C# is of the sources as they were.
    [Fact, Trait("Category", "Unit")]
    public void Sources_changed_since_the_build_are_stale()
    {
        var source = Package_("Contracts");
        File.AppendAllText(Path.Combine(_root, @"Contracts\daml\Kinds\Asset.daml"), "template Asset with\n");

        _sources.StatusOf(Generation("App", source)).ShouldBe(SourceStatus.Stale);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_new_source_file_makes_them_stale_too()
    {
        var source = Package_("Contracts");
        File.WriteAllText(Path.Combine(_root, @"Contracts\daml\Kinds\Note.daml"), "module Kinds.Note where\n");

        _sources.StatusOf(Generation("App", source)).ShouldBe(SourceStatus.Stale);
    }

    // A DAR reference has no sources; a project's sources may be gone from the disk.
    [Fact, Trait("Category", "Unit")]
    public void Without_sources_on_this_machine_the_code_is_external()
    {
        var source = Package_("Contracts");

        _sources.StatusOf(Generation("App", null)).ShouldBe(SourceStatus.External);
        Directory.Delete(Path.Combine(_root, "Contracts"), recursive: true);
        _sources.StatusOf(Generation("App", source)).ShouldBe(SourceStatus.External);
    }

    // Two local packages that both compile to the package: both offered, none picked.
    [Fact, Trait("Category", "Unit")]
    public void Every_local_source_of_a_symbol_is_a_candidate_and_none_is_picked()
    {
        var contracts = Package_("Contracts");
        var copy = Package_("Copy");
        var dar = Generation("Vendor", null);
        var index = BindingIndex.Empty
            .WithDocument("app", At(Generation("App", contracts), "app").FilePath, [At(Generation("App", contracts), "app")])
            .WithDocument("service", At(Generation("Service", copy), "service").FilePath, [At(Generation("Service", copy), "service")])
            .WithDocument("vendor", At(dar, "vendor").FilePath, [At(dar, "vendor")]);
        File.AppendAllText(Path.Combine(_root, @"Copy\daml\Kinds\Asset.daml"), "-- edited\n");

        var candidates = _sources.CandidatesFor(index, Asset);

        candidates.Select(c => (c.ManifestPath, c.Status)).ShouldBe(
            [(contracts.ManifestPath, SourceStatus.Current), (copy.ManifestPath, SourceStatus.Stale)], ignoreOrder: true);
        candidates.Single(c => c.Status == SourceStatus.Current).Locations.Select(l => l.ProjectKey).ShouldBe(["app"]);
    }

    // The same sources built at two times: current as long as one generation matches them.
    [Fact, Trait("Category", "Unit")]
    public void One_source_built_into_several_projects_is_one_candidate()
    {
        var contracts = Package_("Contracts");
        var older = contracts with { Fingerprint = "older" };
        var index = BindingIndex.Empty
            .WithDocument("app", At(Generation("App", contracts), "app").FilePath, [At(Generation("App", contracts), "app")])
            .WithDocument("service", At(Generation("Service", older), "service").FilePath, [At(Generation("Service", older), "service")]);

        var candidate = _sources.CandidatesFor(index, Asset).ShouldHaveSingleItem();

        candidate.Status.ShouldBe(SourceStatus.Current);
        candidate.Locations.Count.ShouldBe(2);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_symbol_generated_only_from_DARs_has_no_candidates()
    {
        var dar = Generation("Vendor", null);

        _sources.CandidatesFor(BindingIndex.Empty.WithDocument("vendor", At(dar, "vendor").FilePath, [At(dar, "vendor")]), Asset).ShouldBeEmpty();
    }
}
