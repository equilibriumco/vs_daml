using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Roslyn.Bindings;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Roslyn.Tests.Bindings;

// The C# a user wrote against the bindings of a Daml declaration, found as Find All References
// finds it, without the generated code's own uses.
public sealed class CSharpReferenceFinderTests : IDisposable
{
    private const string Package = GeneratedCode.KindsPackage;

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
}
