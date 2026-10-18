using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Roslyn.Tests.Bindings;

// Hand-written code using the generated bindings, as a user writes it, with the caret on a name.
public sealed class DamlBindingLookupTests : IDisposable
{
    private const string Package = GeneratedCode.KindsPackage;

    private const string Uses = """
        using Daml.Runtime.Data;
        using Fixture.Kinds.Asset;
        using Fixture.Kinds.Types;

        internal static class Uses
        {
            public static Asset Make(Party party) => new Asset(party, party, "Gold", new Point(0, 0), new Shape.Empty(), Colour.Red, [], null);

            public static string NameOf(Asset asset) => asset.Name;

            public static object Give => Asset.ChoiceGive;

            public static Box<int> Boxed => new(1, "one");

            public static Point Named => new(X: 1, Y: 2);
        }
        """;

    private readonly GenerationsOnDisk _disk = new();
    private readonly TestSolution _solution = new();
    private readonly GenerationCatalog _catalog = new();
    private readonly ProjectId _app;
    private readonly string _usesPath;

    public DamlBindingLookupTests()
    {
        _usesPath = Path.Combine(_disk.Root, @"App\Uses.cs");
        _app = _solution.Add("App", _disk.Publish("App").Append((_usesPath, Uses)));
    }

    public void Dispose()
    {
        _solution.Dispose();
        _disk.Dispose();
    }

    private static DamlSymbolId Symbol(DamlSymbolKind kind, string module, string entity, string? member = null) => new(Package, module, entity, member, kind);

    // The caret inside the occurrence-th text, offset characters in.
    private async Task<BindingsAtPosition?> At(string text, int occurrence = 1, int offset = 1, ProjectId? project = null, string? path = null)
    {
        var document = _solution.Solution.GetProject(project ?? _app)!.Documents.Single(d => d.FilePath == (path ?? _usesPath));
        var source = (await document.GetTextAsync()).ToString();
        var position = -1;
        for (var i = 0; i < occurrence; i++)
        {
            position = source.IndexOf(text, position + 1, StringComparison.Ordinal);
        }
        position.ShouldBeGreaterThanOrEqualTo(0, text);
        var index = await BindingIndexBuilder.BuildAsync(_solution.Solution, _catalog);
        return await DamlBindingLookup.FindAsync(document, position + offset, index);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Asset Make", 1, DamlSymbolKind.Template, "Kinds.Asset", "Asset", null)]
    [InlineData("new Asset", 1, DamlSymbolKind.Template, "Kinds.Asset", "Asset", null)]
    [InlineData("asset.Name", 1, DamlSymbolKind.Field, "Kinds.Asset", "Asset", "name")]
    [InlineData("ChoiceGive", 1, DamlSymbolKind.Choice, "Kinds.Asset", "Asset", "Give")]
    [InlineData("Colour.Red", 1, DamlSymbolKind.Constructor, "Kinds.Types", "Colour", "Red")]
    [InlineData("Shape.Empty", 1, DamlSymbolKind.Constructor, "Kinds.Types", "Shape", "Empty")]
    [InlineData("Box<int>", 1, DamlSymbolKind.Record, "Kinds.Types", "Box", null)]
    [InlineData("new Point(0", 1, DamlSymbolKind.Record, "Kinds.Types", "Point", null)]
    [InlineData("X: 1", 1, DamlSymbolKind.Field, "Kinds.Types", "Point", "x")]
    public async Task A_name_in_hand_written_code_leads_to_the_Daml_symbol_it_uses(string text, int occurrence, DamlSymbolKind kind, string module, string entity, string? member)
    {
        var offset = text.StartsWith("new ", StringComparison.Ordinal) ? 5 : text.Contains('.') ? text.IndexOf('.') + 1 : 1;
        if (text == "asset.Name")
        {
            offset = 7;
        }

        var found = (await At(text, occurrence, offset)).ShouldNotBeNull(text);

        found.Symbols.ShouldBe([Symbol(kind, module, entity, member)]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_span_is_the_name_the_caret_is_on_even_at_its_end()
    {
        var inside = (await At("Asset Make", offset: 2)).ShouldNotBeNull();
        var atEnd = (await At("Asset Make", offset: 5)).ShouldNotBeNull();

        atEnd.Span.ShouldBe(inside.Span);
        inside.Span.Length.ShouldBe("Asset".Length);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Party party")]
    [InlineData("Uses")]
    [InlineData("NameOf")]
    [InlineData("\"Gold\"")]
    public async Task Names_that_arent_generated_Daml_lead_nowhere(string text) =>
        (await At(text, offset: 1)).ShouldBeNull(text);

    // The bindings are generated in one project and used in another that references it.
    [Fact, Trait("Category", "Unit")]
    public async Task A_name_in_another_project_leads_to_the_project_that_generated_it()
    {
        var libraryPath = Path.Combine(_disk.Root, @"Library\Report.cs");
        var library = _solution.Add("Library", [(libraryPath, "internal static class Report { public static string Of(Fixture.Kinds.Asset.Asset asset) => asset.Owner.ToString(); }")]);
        _solution.Change(s => s.AddProjectReference(library, new ProjectReference(_app)));

        var found = (await At("Asset asset", offset: 1, project: library, path: libraryPath)).ShouldNotBeNull();

        found.Symbols.ShouldBe([Symbol(DamlSymbolKind.Template, "Kinds.Asset", "Asset")]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_an_index_nothing_is_looked_up()
    {
        var document = _solution.Solution.GetProject(_app)!.Documents.Single(d => d.FilePath == _usesPath);

        (await DamlBindingLookup.FindAsync(document, 10, BindingIndex.Empty)).ShouldBeNull();
    }
}
