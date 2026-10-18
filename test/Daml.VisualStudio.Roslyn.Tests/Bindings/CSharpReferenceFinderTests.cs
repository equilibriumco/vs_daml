using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis.Text;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Roslyn.Tests.Bindings;

// The C# a user wrote against the bindings of a Daml declaration, found as Find All References
// finds it, without the generated code's own uses.
public sealed class CSharpReferenceFinderTests : IDisposable
{
    private const string Package = GeneratedCode.KindsPackage;
    private const string Other = "1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb";
    private const string Id = "9de80d6cfe50bada";

    private const string Uses = """
        using Daml.Runtime.Data;
        using Fixture.Kinds.Asset;

        internal static class Uses
        {
            public static Asset Make(Party party) => new Asset(party, party, "Gold", new Fixture.Kinds.Types.Point(0, 0), new Fixture.Kinds.Types.Shape.Empty(), Fixture.Kinds.Types.Colour.Red, [], null);

            public static object Give => Asset.ChoiceGive;
        }
        """;

    private readonly GenerationsOnDisk _disk = new();
    private readonly TestSolution _solution = new();
    private readonly GenerationCatalog _catalog = new();
    private readonly string _usesPath;

    public CSharpReferenceFinderTests() => _usesPath = Path.Combine(_disk.Root, @"Shared\Uses.cs");

    public void Dispose()
    {
        _solution.Dispose();
        _disk.Dispose();
    }

    private static DamlSymbolId Symbol(DamlSymbolKind kind, string entity, string? member = null) => new(Package, "Kinds.Asset", entity, member, kind);

    private async Task<CSharpReference[]> FindAsync(DamlSymbolId symbol)
    {
        var index = await BindingIndexBuilder.BuildAsync(_solution.Solution, _catalog);
        return [.. await CSharpReferenceFinder.FindAsync(_solution.Solution, index.LocationsOf(symbol), _catalog)];
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_template_is_used_where_the_code_names_its_type_and_never_in_the_generated_code()
    {
        _solution.Add("App", _disk.Publish("App").Append((_usesPath, Uses)));

        var references = await FindAsync(Symbol(DamlSymbolKind.Template, "Asset"));

        references.ShouldNotBeEmpty();
        references.ShouldAllBe(r => r.FilePath == _usesPath && r.Project == "App");
        references.Select(r => r.Line).Distinct().ShouldBe([6, 8]);
        references.First().Code.ShouldStartWith("public static Asset Make(Party party)");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_choice_is_used_where_the_code_takes_its_property()
    {
        _solution.Add("App", _disk.Publish("App").Append((_usesPath, Uses)));

        var reference = (await FindAsync(Symbol(DamlSymbolKind.Choice, "Asset", "Give"))).ShouldHaveSingleItem();

        (reference.Line, reference.Code).ShouldBe((8, "public static object Give => Asset.ChoiceGive;"));
    }

    // A file linked into two projects: each place once, with both projects named.
    [Fact, Trait("Category", "Unit")]
    public async Task A_linked_file_counts_each_place_once()
    {
        _solution.Add("App", _disk.Publish("App").Append((_usesPath, Uses)));
        _solution.Add("Web", _disk.Publish("Web").Append((_usesPath, Uses)));

        var reference = (await FindAsync(Symbol(DamlSymbolKind.Choice, "Asset", "Give"))).ShouldHaveSingleItem();

        reference.Project.ShouldBe("App, Web");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Bindings_nobody_uses_have_no_references()
    {
        _solution.Add("App", _disk.Publish("App"));

        (await FindAsync(Symbol(DamlSymbolKind.Template, "Asset"))).ShouldBeEmpty();
    }

    // A build republished the generation with a new package while the editor still holds the code of
    // the build before: the uses are those of the code the generator wrote, not a false 0.
    [Fact, Trait("Category", "Unit")]
    public async Task Uses_are_found_while_the_editor_still_holds_the_previous_build()
    {
        _solution.Add("App", _disk.Publish("App", id: Id).Append((_usesPath, Uses)));
        _disk.Publish("App", package: Other, id: Id);
        var index = await BindingIndexBuilder.BuildAsync(_solution.Solution, _catalog);

        var references = await CSharpReferenceFinder.FindAsync(
            _solution.Solution, index.LocationsOf(new DamlSymbolId(Other, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template)), _catalog);

        references.Select(r => r.Line).Distinct().ShouldBe([6, 8]);
    }

    // The code changed since the index read it and no longer stands for the template: no count beats a false 0.
    [Fact, Trait("Category", "Unit")]
    public async Task Declarations_that_no_longer_hold_cant_be_counted()
    {
        _solution.Add("App", _disk.Publish("App").Append((_usesPath, Uses)));
        var index = await BindingIndexBuilder.BuildAsync(_solution.Solution, _catalog);
        var asset = _solution.Solution.Projects.Single().Documents.Single(d => d.FilePath!.EndsWith(@"\Asset\Asset.cs", StringComparison.Ordinal));
        var edited = _solution.Solution.WithDocumentText(asset.Id, SourceText.From("namespace Fixture.Kinds.Asset;"));

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => CSharpReferenceFinder.FindAsync(edited, index.LocationsOf(Symbol(DamlSymbolKind.Template, "Asset")), _catalog));

        failure.Message.ShouldContain("build the project to bring its Daml bindings up to date");
    }
}
