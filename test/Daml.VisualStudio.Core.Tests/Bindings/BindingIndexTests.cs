using System.Linq;
using Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Bindings;

public class BindingIndexTests
{
    private const string Package = "bd2aa85e017dad908dbd9c8103929df3edaa7b0961b675d3ef216644565f9cb1";
    private const string App = "app-project";
    private const string Service = "service-project";
    private const string AssetFile = @"C:\work\App\obj\Debug\net10.0\Daml\Contracts-9de80d6cfe50bada\Fixture\Kinds\Asset\Asset.cs";
    private const string NoteFile = @"C:\work\App\obj\Debug\net10.0\Daml\Contracts-9de80d6cfe50bada\Fixture\Kinds\Asset\Note.cs";

    private static readonly DamlSymbolId Asset = new(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template);
    private static readonly DamlSymbolId Give = new(Package, "Kinds.Asset", "Asset", "Give", DamlSymbolKind.Choice);
    private static readonly DamlSymbolId Note = new(Package, "Kinds.Asset", "Note", null, DamlSymbolKind.Template);

    private static readonly GenerationProvenance Generation = GenerationProvenance.From(
        @"C:\work\App\obj\Debug\net10.0\Daml\Contracts-9de80d6cfe50bada", GenerationProvenanceTests.Manifest())!;

    private static BindingLocation At(DamlSymbolId symbol, string project, string file, string declarationId, int start, string? framework = null) =>
        new(symbol, project, framework, Generation, declarationId, file, start, 5);

    private static BindingIndex WithAsset(BindingIndex index, string project, string? framework = null) =>
        index.WithDocument(project, AssetFile,
        [
            At(Asset, project, AssetFile, "T:Fixture.Kinds.Asset.Asset", 100, framework),
            At(Give, project, AssetFile, "P:Fixture.Kinds.Asset.Asset.ChoiceGive", 300, framework),
        ]);

    // A Daml file's lenses start from the generations built from its package's sources.
    [Fact, Trait("Category", "Unit")]
    public void The_index_names_the_generations_built_from_a_Daml_project()
    {
        var index = WithAsset(WithAsset(BindingIndex.Empty, App), Service, "net10.0");

        index.GenerationsFrom(@"c:\work\contracts\daml.yaml").ShouldBe([Generation]);
        index.GenerationsFrom(@"C:\work\Other\daml.yaml").ShouldBeEmpty();
    }

    // A line for the log whenever what the index holds changes, to tell why a lens or command went away.
    [Fact, Trait("Category", "Unit")]
    public void The_index_describes_what_it_holds()
    {
        BindingIndex.Empty.Describe().ShouldBe("no C# declarations");
        WithAsset(WithAsset(BindingIndex.Empty, App), Service, "net10.0").Describe()
            .ShouldBe($"4 C# declarations from Contracts-9de80d6cfe50bada (package {Generation.MainPackageId.Substring(0, 8)})");
        WithAsset(BindingIndex.Empty, App).WithDocument(App, AssetFile, [At(Asset, App, AssetFile, "T:Fixture.Kinds.Asset.Asset", 100)]).Describe()
            .ShouldBe($"1 C# declaration from Contracts-9de80d6cfe50bada (package {Generation.MainPackageId.Substring(0, 8)})");
    }

    [Fact, Trait("Category", "Unit")]
    public void An_empty_index_knows_nothing()
    {
        BindingIndex.Empty.LocationsOf(Asset).ShouldBeEmpty();
        BindingIndex.Empty.SymbolsOf(App, "T:Fixture.Kinds.Asset.Asset").ShouldBeEmpty();
        (BindingIndex.Empty.Version, BindingIndex.Empty.Count).ShouldBe((0L, 0));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_symbol_leads_to_its_declarations_and_a_declaration_back_to_its_symbol()
    {
        var index = WithAsset(BindingIndex.Empty, App);

        index.LocationsOf(Give).Select(l => (l.ProjectKey, l.DeclarationId, l.Start)).ShouldBe([(App, "P:Fixture.Kinds.Asset.Asset.ChoiceGive", 300)]);
        index.SymbolsOf(App, "T:Fixture.Kinds.Asset.Asset").ShouldBe([Asset]);
        index.SymbolsOf(Service, "T:Fixture.Kinds.Asset.Asset").ShouldBeEmpty();
        index.Count.ShouldBe(2);
    }

    // Every change makes a new index; whoever holds the old one keeps a consistent view.
    [Fact, Trait("Category", "Unit")]
    public void Changes_make_new_versions_and_leave_the_old_ones_as_they_were()
    {
        var first = WithAsset(BindingIndex.Empty, App);
        var second = first.WithDocument(App, NoteFile, [At(Note, App, NoteFile, "T:Fixture.Kinds.Asset.Note", 50)]);

        (first.Version, second.Version).ShouldBe((1L, 2L));
        first.LocationsOf(Note).ShouldBeEmpty();
        second.LocationsOf(Note).ShouldHaveSingleItem();
        second.LocationsOf(Asset).ShouldHaveSingleItem();
    }

    // Versions only go up, even when the whole solution is read from scratch.
    [Fact, Trait("Category", "Unit")]
    public void A_cleared_index_is_empty_and_newer()
    {
        var index = WithAsset(BindingIndex.Empty, App);

        var cleared = index.Cleared();

        (cleared.Count, cleared.Version).ShouldBe((0, index.Version + 1));
    }

    [Fact, Trait("Category", "Unit")]
    public void Several_documents_change_in_one_version()
    {
        var index = WithAsset(BindingIndex.Empty, App).WithDocuments(
        [
            (App, AssetFile, []),
            (App, NoteFile, [At(Note, App, NoteFile, "T:Fixture.Kinds.Asset.Note", 50)]),
        ]);

        index.Version.ShouldBe(2L);
        (index.LocationsOf(Asset).Count, index.LocationsOf(Note).Count).ShouldBe((0, 1));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_location_of_another_document_than_the_one_given_is_refused() =>
        Should.Throw<System.ArgumentException>(() => BindingIndex.Empty.WithDocument(Service, AssetFile, [At(Asset, App, AssetFile, "T:Fixture.Kinds.Asset.Asset", 100)]));

    [Fact, Trait("Category", "Unit")]
    public void A_document_read_again_replaces_what_it_had()
    {
        var index = WithAsset(BindingIndex.Empty, App)
            .WithDocument(App, AssetFile, [At(Asset, App, AssetFile, "T:Fixture.Kinds.Asset.Asset", 120)]);

        index.LocationsOf(Give).ShouldBeEmpty();
        index.LocationsOf(Asset).ShouldHaveSingleItem().Start.ShouldBe(120);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_removed_document_or_project_takes_its_declarations_with_it()
    {
        var index = WithAsset(WithAsset(BindingIndex.Empty, App), Service)
            .WithDocument(App, NoteFile, [At(Note, App, NoteFile, "T:Fixture.Kinds.Asset.Note", 50)]);

        index.WithoutDocument(App, NoteFile).LocationsOf(Note).ShouldBeEmpty();
        index.WithoutProject(App).LocationsOf(Asset).Select(l => l.ProjectKey).ShouldBe([Service]);
        index.WithoutProject(App).LocationsOf(Note).ShouldBeEmpty();
        index.WithoutProject("unknown").ShouldBeSameAs(index);
        index.WithoutDocument(App, @"C:\elsewhere.cs").ShouldBeSameAs(index);
    }

    // A file linked into two projects is one declaration found in both.
    [Fact, Trait("Category", "Unit")]
    public void A_linked_file_is_one_declaration_in_each_project_that_has_it()
    {
        var index = WithAsset(WithAsset(BindingIndex.Empty, App), Service);

        var declaration = index.DeclarationsOf(Asset).ShouldHaveSingleItem();

        (declaration.FilePath, declaration.DeclarationId, declaration.Start).ShouldBe((AssetFile, "T:Fixture.Kinds.Asset.Asset", 100));
        declaration.Locations.Select(l => l.ProjectKey).ShouldBe([App, Service], ignoreOrder: true);
    }

    // Each target framework has its own generation, so its own files: separate declarations.
    [Fact, Trait("Category", "Unit")]
    public void Each_target_framework_has_declarations_of_its_own()
    {
        const string Net11 = @"C:\work\App\obj\Debug\net11.0\Daml\Contracts-9de80d6cfe50bada\Fixture\Kinds\Asset\Asset.cs";
        var index = WithAsset(BindingIndex.Empty, App + "-net10", "net10.0")
            .WithDocument(App + "-net11", Net11, [At(Asset, App + "-net11", Net11, "T:Fixture.Kinds.Asset.Asset", 100, "net11.0")]);

        index.DeclarationsOf(Asset).Select(d => (d.FilePath, d.Locations.Single().TargetFramework)).ShouldBe(
            [(AssetFile, "net10.0"), (Net11, "net11.0")], ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public void Paths_are_compared_as_Windows_compares_them()
    {
        var index = WithAsset(BindingIndex.Empty, App);

        index.WithoutDocument(App, AssetFile.ToUpperInvariant()).LocationsOf(Asset).ShouldBeEmpty();
        index.WithDocument(App, AssetFile.ToLowerInvariant(), []).Count.ShouldBe(0);
    }
}
