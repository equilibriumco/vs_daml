using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Roslyn.Bindings;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Roslyn.Tests.Bindings;

public sealed class BindingReaderTests : IDisposable
{
    private const string Package = GeneratedCode.KindsPackage;
    private const string Other = "1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb";

    private readonly GenerationsOnDisk _disk = new();
    private readonly GenerationCatalog _catalog = new();

    public void Dispose() => _disk.Dispose();

    private List<DocumentBindings> Read(IEnumerable<(string Path, string Text)> files)
    {
        var compilation = GeneratedCode.Compile(files);
        return compilation.SyntaxTrees.Select(t => BindingReader.Read(compilation.GetSemanticModel(t), _catalog)).ToList();
    }

    private static List<DamlBinding> Bindings(List<DocumentBindings> documents) => documents.SelectMany(d => d.Bindings).ToList();

    private static List<RejectedDeclaration> Rejected(List<DocumentBindings> documents) => documents.SelectMany(d => d.Rejected).ToList();

    [Fact, Trait("Category", "Unit")]
    public void Data_types_and_their_members_are_of_the_generations_package_in_the_module_under_the_prefix()
    {
        var documents = Read(_disk.Publish("App"));

        Rejected(documents).ShouldBeEmpty();
        Bindings(documents).Where(b => b.Symbol.Module == "Kinds.Types").Select(b => (b.Symbol, b.DeclarationId)).ShouldContain(
            (new DamlSymbolId(Package, "Kinds.Types", "Point", "x", DamlSymbolKind.Field), "P:Fixture.Kinds.Types.Point.X"));
        Bindings(documents).Select(b => b.Symbol).ShouldContain(new DamlSymbolId(Package, "Kinds.Types", "Shape", "Circle.radius", DamlSymbolKind.Field));
        Bindings(documents).Select(b => b.Symbol).ShouldContain(new DamlSymbolId(Package, "Kinds.Types", "Colour", "Red", DamlSymbolKind.Constructor));
        Bindings(documents).Select(b => b.Symbol).ShouldContain(new DamlSymbolId(Package, "Kinds.Holding", "HoldingView", null, DamlSymbolKind.Record));
        Bindings(documents).ShouldAllBe(b => b.Generation.Namespace == "Fixture" && b.Generation.MainPackageId == Package);
    }

    [Fact, Trait("Category", "Unit")]
    public void Templates_interfaces_and_their_members_keep_what_their_code_states()
    {
        var symbols = Bindings(Read(_disk.Publish("App"))).Select(b => b.Symbol).ToList();

        symbols.ShouldContain(new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template));
        symbols.ShouldContain(new DamlSymbolId(Package, "Kinds.Asset", "Asset", "Give.newOwner", DamlSymbolKind.Field));
        symbols.ShouldContain(new DamlSymbolId(Package, "Kinds.Holding", "Holding", "Transfer", DamlSymbolKind.Choice));
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_a_namespace_prefix_the_namespaces_are_the_modules()
    {
        var prefixed = Bindings(Read(_disk.Publish("App"))).Select(b => b.Symbol).ToList();
        var plain = Read(_disk.Publish("Plain", @namespace: null));

        Rejected(plain).ShouldBeEmpty();
        Bindings(plain).Select(b => b.Symbol).ShouldBe(prefixed, ignoreOrder: true);
        Bindings(plain).ShouldContain(b => b.DeclarationId == "T:Kinds.Types.Point");
    }

    // The same DAR in two projects, each with a namespace of its own: one symbol, a declaration per project.
    [Fact, Trait("Category", "Unit")]
    public void Two_projects_bind_the_same_symbols_to_their_own_declarations()
    {
        var first = Bindings(Read(_disk.Publish("App")));
        var second = Bindings(Read(_disk.Publish("Service", @namespace: "Acme.Ledger")));

        second.Select(b => b.Symbol).ShouldBe(first.Select(b => b.Symbol), ignoreOrder: true);
        second.Single(b => b.Symbol == new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template)).DeclarationId
            .ShouldBe("T:Acme.Ledger.Kinds.Asset.Asset");
    }

    // Module and entity don't identify a declaration: two DARs, two versions, can both have Kinds.Asset:Asset.
    [Fact, Trait("Category", "Unit")]
    public void The_same_module_and_entity_from_two_packages_are_two_symbols()
    {
        var bindings = Bindings(Read(_disk.Publish("App").Concat(_disk.Publish("App", @namespace: "Second", package: Other))));

        bindings.Where(b => b.Symbol.Kind == DamlSymbolKind.Template && b.Symbol.Entity == "Asset").Select(b => (b.Symbol.PackageId, b.DeclarationId)).ShouldBe(
            [(Package, "T:Fixture.Kinds.Asset.Asset"), (Other, "T:Second.Kinds.Asset.Asset")], ignoreOrder: true);
        bindings.Where(b => b.Symbol.Entity == "Point" && b.Symbol.Kind == DamlSymbolKind.Record).Select(b => b.Symbol.PackageId).ShouldBe([Package, Other], ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_file_changed_since_its_generation_was_published_stands_for_nothing()
    {
        var files = _disk.Publish("App");
        var point = files.Single(f => f.Path.EndsWith(@"\Point.cs", StringComparison.Ordinal));
        File.AppendAllText(point.Path, "// edited\n");

        var documents = Read(files);

        documents.Single(d => d.Rejected.Any(r => r.Declaration.FilePath == point.Path)).Bindings.ShouldBeEmpty();
        Rejected(documents).ShouldAllBe(r => r.Declaration.FilePath == point.Path && r.Problem.Contains("changed after it was generated"));
    }

    // Code that names another package than the generation's is from another build.
    [Fact, Trait("Category", "Unit")]
    public void Code_naming_another_package_than_the_manifest_is_rejected()
    {
        var documents = Read(_disk.Publish("App", manifestPackage: Other));

        Rejected(documents).Select(r => r.Declaration.Name.Kind).Distinct().ShouldBe(
            [DamlSymbolKind.Template, DamlSymbolKind.Interface, DamlSymbolKind.Choice, DamlSymbolKind.Field], ignoreOrder: true);
        Rejected(documents).ShouldAllBe(r => r.Declaration.Name.PackageId == Package && r.Problem.Contains(Other));
        Bindings(documents).ShouldAllBe(b => b.Symbol.PackageId == Other && b.Symbol.Module != "Kinds.Asset");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_generation_that_doesnt_record_its_package_stands_for_nothing()
    {
        var documents = Read(_disk.Publish("App", recordPackage: false));

        Bindings(documents).ShouldBeEmpty();
        Rejected(documents).ShouldNotBeEmpty();
        Rejected(documents).ShouldAllBe(r => r.Problem.Contains("doesn't record which package"));
    }

    // Generated-looking code someone put next to the generator's, or anywhere else, isn't the generator's.
    [Fact, Trait("Category", "Unit")]
    public void Files_the_manifest_doesnt_list_have_no_bindings()
    {
        var files = _disk.Publish("App");
        var point = files.Single(f => f.Path.EndsWith(@"\Point.cs", StringComparison.Ordinal));
        var mine = (Path: Path.Combine(Path.GetDirectoryName(point.Path)!, "Mine.cs"), Text: point.Text.Replace("Point", "Mine"));
        var program = (Path: Path.Combine(_disk.Root, @"App\Program.cs"), Text: point.Text.Replace("Point", "Program"));
        File.WriteAllText(mine.Path, mine.Text);
        File.WriteAllText(program.Path, program.Text);

        var documents = Read([mine, program]);

        documents.ShouldAllBe(d => d.Bindings.Count == 0 && d.Rejected.Count == 0);
    }
}
