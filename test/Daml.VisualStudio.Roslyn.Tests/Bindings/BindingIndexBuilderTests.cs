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

public sealed class BindingIndexBuilderTests : IDisposable
{
    private const string Package = GeneratedCode.KindsPackage;

    private static readonly DamlSymbolId Asset = new(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template);
    private static readonly DamlSymbolId Point = new(Package, "Kinds.Types", "Point", null, DamlSymbolKind.Record);

    private readonly GenerationsOnDisk _disk = new();
    private readonly TestSolution _solution = new();
    private readonly GenerationCatalog _catalog = new();

    public void Dispose()
    {
        _solution.Dispose();
        _disk.Dispose();
    }

    private Task<BindingIndex> Build() => BindingIndexBuilder.BuildAsync(_solution.Solution, _catalog);

    private (string Path, string Text) Program(string project) =>
        (Path.Combine(_disk.Root, project, "Program.cs"), "internal static class Program { }");

    [Fact, Trait("Category", "Unit")]
    public async Task A_projects_generated_files_are_indexed_under_the_project()
    {
        var app = _solution.Add("App", _disk.Publish("App").Append(Program("App")));

        var index = await Build();

        var key = BindingIndexBuilder.ProjectKey(app);
        index.SymbolsOf(key, "T:Fixture.Kinds.Asset.Asset").ShouldBe([Asset]);
        index.SymbolsOf(key, "P:Fixture.Kinds.Asset.Asset.ChoiceGive").ShouldBe([new DamlSymbolId(Package, "Kinds.Asset", "Asset", "Give", DamlSymbolKind.Choice)]);
        var point = index.LocationsOf(Point).ShouldHaveSingleItem();
        (point.ProjectKey, point.TargetFramework, point.DeclarationId).ShouldBe((key, (string?)null, "T:Fixture.Kinds.Types.Point"));
        point.FilePath.ShouldEndWith(@"\Fixture\Kinds\Types\Point.cs");
    }

    // Without the Daml runtime a project can't have bindings that compile: it isn't even looked through.
    [Fact, Trait("Category", "Unit")]
    public async Task A_project_without_the_Daml_runtime_isnt_read()
    {
        _solution.Add("App", _disk.Publish("App"), withDamlRuntime: false);

        (await Build()).Count.ShouldBe(0);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_project_without_generated_files_adds_nothing()
    {
        _solution.Add("Library", [Program("Library")]);

        (await Build()).Count.ShouldBe(0);
    }

    // Visual Studio loads a multi-targeting project once per framework, each with its own generation.
    [Fact, Trait("Category", "Unit")]
    public async Task Each_target_framework_has_its_own_declarations()
    {
        _solution.Add("App (net10.0)", _disk.Publish("App"));
        _solution.Add("App (net11.0)", _disk.Publish("App", framework: "net11.0"));

        var index = await Build();

        index.DeclarationsOf(Asset).Select(d => (d.Locations.Single().TargetFramework, d.FilePath.Contains(@"\net11.0\"))).ShouldBe(
            [("net10.0", false), ("net11.0", true)], ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_file_linked_into_two_projects_is_one_declaration_in_both()
    {
        var files = _disk.Publish("App");
        var app = _solution.Add("App", files);
        var tests = _solution.Add("App.Tests", files.Where(f => f.Path.Contains(@"\Asset\")));

        var declaration = (await Build()).DeclarationsOf(Asset).ShouldHaveSingleItem();

        declaration.Locations.Select(l => l.ProjectKey).ShouldBe([BindingIndexBuilder.ProjectKey(app), BindingIndexBuilder.ProjectKey(tests)], ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_project_gone_from_the_solution_is_gone_from_the_index()
    {
        var app = _solution.Add("App", _disk.Publish("App"));
        _solution.Add("Service", _disk.Publish("Service", @namespace: "Acme"));
        var before = await Build();
        _solution.Change(s => s.RemoveProject(app));

        var after = await Build();
        var updated = before.WithoutProject(BindingIndexBuilder.ProjectKey(app));

        before.LocationsOf(Asset).Count.ShouldBe(2);
        after.LocationsOf(Asset).Select(l => l.DeclarationId).ShouldBe(["T:Acme.Kinds.Asset.Asset"]);
        updated.LocationsOf(Asset).Select(l => l.DeclarationId).ShouldBe(["T:Acme.Kinds.Asset.Asset"]);
    }

    // Bindings shipped compiled, as a NuGet package without XML docs: nothing ties them to Daml sources.
    [Fact, Trait("Category", "Unit")]
    public async Task Bindings_referenced_as_a_compiled_assembly_arent_indexed()
    {
        var assembly = GeneratedCode.Compile(GeneratedCode.Files(GeneratedCode.Kinds));
        using var image = new MemoryStream();
        assembly.Emit(image).Success.ShouldBeTrue();
        var app = _solution.Add("App", [(Path.Combine(_disk.Root, @"App\Uses.cs"), "internal static class Uses { public static Fixture.Kinds.Types.Point Origin => new(0, 0); }")],
            [MetadataReference.CreateFromImage(image.ToArray())]);

        var index = await Build();

        (await _solution.Solution.GetProject(app)!.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        index.Count.ShouldBe(0);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_document_read_again_replaces_its_declarations()
    {
        var app = _solution.Add("App", _disk.Publish("App"));
        var index = await Build();
        var asset = _solution.Solution.GetProject(app)!.Documents.Single(d => d.FilePath!.EndsWith(@"\Asset\Asset.cs", StringComparison.Ordinal));
        _solution.Change(s => s.WithDocumentText(asset.Id, Microsoft.CodeAnalysis.Text.SourceText.From("namespace Fixture.Kinds.Asset;")));

        var updated = await BindingIndexBuilder.WithDocumentsAsync(index, [_solution.Solution.GetDocument(asset.Id)!], _catalog);

        updated.Version.ShouldBe(index.Version + 1);
        updated.LocationsOf(Asset).ShouldBeEmpty();
        updated.LocationsOf(Point).ShouldHaveSingleItem();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("App (net10.0)", "net10.0")]
    [InlineData("My App (net48)", "net48")]
    [InlineData("App", null)]
    [InlineData("App (old)", "old")]
    public void A_project_names_its_framework_only_when_loaded_once_per_framework(string name, string? framework)
    {
        var id = _solution.Add(name, []);

        BindingIndexBuilder.TargetFramework(_solution.Solution.GetProject(id)!).ShouldBe(framework);
        BindingIndexBuilder.Project(_solution.Solution, BindingIndexBuilder.ProjectKey(id))!.Id.ShouldBe(id);
        BindingIndexBuilder.Project(_solution.Solution, "not a key").ShouldBeNull();
    }
}
