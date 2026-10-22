using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Tests.Bindings;

public sealed class BindingResolverTests : IDisposable
{
    private const string Package = GeneratedCode.KindsPackage;

    private readonly GenerationsOnDisk _disk = new();
    private readonly TestSolution _solution = new();
    private readonly GenerationCatalog _catalog = new();
    private readonly ProjectId _app;

    public BindingResolverTests() => _app = _solution.Add("App", _disk.Publish("App").Append((Path.Combine(_disk.Root, @"App\Program.cs"), "internal static class Program { }")));

    public void Dispose()
    {
        _solution.Dispose();
        _disk.Dispose();
    }

    private Task<BindingIndex> Build() => BindingIndexBuilder.BuildAsync(_solution.Solution, _catalog);

    private Task<ResolvedBinding> Resolve(BindingLocation location) => BindingResolver.ResolveAsync(_solution.Solution, location, _catalog);

    private static BindingLocation Of(BindingIndex index, DamlSymbolId symbol, string declarationId) =>
        index.LocationsOf(symbol).Single(l => l.DeclarationId == declarationId);

    private Document DocumentEndingWith(string end) =>
        _solution.Solution.GetProject(_app)!.Documents.Single(d => d.FilePath!.EndsWith(end, StringComparison.Ordinal));

    // The index read earlier, the solution edited since in ways unrelated to the bindings.
    [Fact, Trait("Category", "Unit")]
    public async Task Every_location_resolves_after_unrelated_edits()
    {
        var index = await Build();
        var program = DocumentEndingWith(@"\Program.cs");
        _solution.Change(s => s
            .WithDocumentText(program.Id, SourceText.From("internal static class Program { public static int Answer => 42; }"))
            .AddDocument(DocumentId.CreateNewId(_app), "More.cs", SourceText.From("internal sealed class More { }"), filePath: Path.Combine(_disk.Root, @"App\More.cs")));

        foreach (var symbol in index.LocationsOf(new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template))
            .Concat(index.LocationsOf(new DamlSymbolId(Package, "Kinds.Types", "Shape", "Circle", DamlSymbolKind.Constructor))))
        {
            var resolved = await Resolve(symbol);
            resolved.Problem.ShouldBeNull();
            DocumentationCommentId.CreateDeclarationId(resolved.Symbol!).ShouldBe(symbol.DeclarationId);
        }
        index.Count.ShouldBeGreaterThan(40);
    }

    // The part of the partial template with TemplateId and the choices is gone; the part with the
    // choice argument is still there, so its type exists, but nothing says it is the template's.
    [Fact, Trait("Category", "Unit")]
    public async Task A_symbol_that_outlived_its_binding_is_told_apart_from_one_that_is_gone()
    {
        var index = await Build();
        var asset = new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template);
        var give = new DamlSymbolId(Package, "Kinds.Asset", "Asset", "Give", DamlSymbolKind.Choice);
        var note = new DamlSymbolId(Package, "Kinds.Asset", "Note", null, DamlSymbolKind.Template);
        _solution.Change(s => s.RemoveDocument(DocumentEndingWith(@"\Asset\Asset.cs").Id));

        var template = await Resolve(Of(index, asset, "T:Fixture.Kinds.Asset.Asset"));
        var choiceProperty = await Resolve(Of(index, give, "P:Fixture.Kinds.Asset.Asset.ChoiceGive"));
        var choiceArgument = await Resolve(Of(index, give, "T:Fixture.Kinds.Asset.Asset.Give"));
        var other = await Resolve(Of(index, note, "T:Fixture.Kinds.Asset.Note"));

        (template.Symbol, template.Problem).ShouldBe((null, $"T:Fixture.Kinds.Asset.Asset no longer stands for {asset}; build the project to bring its Daml bindings up to date."));
        choiceProperty.Symbol.ShouldBeNull();
        choiceProperty.Problem.ShouldNotBeNull().ShouldContain("isn't in App any more");
        choiceArgument.Symbol.ShouldBeNull();
        choiceArgument.Problem.ShouldNotBeNull().ShouldContain("no longer stands for");
        other.Symbol.ShouldNotBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_location_of_a_project_no_longer_in_the_solution_doesnt_resolve()
    {
        var index = await Build();
        var location = index.LocationsOf(new DamlSymbolId(Package, "Kinds.Types", "Point", null, DamlSymbolKind.Record)).Single();
        _solution.Change(s => s.RemoveProject(_app));

        (await Resolve(location)).Problem.ShouldBe("The project that had it isn't in the solution any more.");
    }
}
