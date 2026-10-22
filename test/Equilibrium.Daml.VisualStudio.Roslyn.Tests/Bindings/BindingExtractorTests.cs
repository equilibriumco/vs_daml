using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Tests.Bindings;

public class BindingExtractorTests
{
    private const string Package = GeneratedCode.KindsPackage;

    private static List<GeneratedDeclaration> Extract(CSharpCompilation compilation) =>
        compilation.SyntaxTrees.SelectMany(t => BindingExtractor.Extract(compilation.GetSemanticModel(t))).ToList();

    private static readonly List<GeneratedDeclaration> Kinds = Extract(GeneratedCode.Compile(GeneratedCode.Files(GeneratedCode.Kinds)));

    [Fact, Trait("Category", "Unit")]
    public void Real_emitter_names_resolve_prefix_elision_and_escaped_identifiers()
    {
        var fixture = Path.Combine(Repository.Root, @"test\fixtures\bindings\names\csharp");
        var compilation = GeneratedCode.Compile(GeneratedCode.Files(fixture));
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var declarations = Extract(compilation);
        var package = declarations.First(d => d.Name.PackageId is not null).Name.PackageId!;
        var provenance = new GenerationProvenance(fixture, "names", package, [], "Names", "App.csproj", "Contracts.msbuildproj",
            Equilibrium.Daml.Build.Core.Codegen.DamlReferenceKind.Project, null)
        {
            Modules = ["Names", "Names.More", "Names$u0027.Types"],
        };
        var resolved = declarations.Select(d => provenance.Resolve(d.Name)).ToList();
        resolved.ShouldAllBe(r => r.Symbol != null);
        var symbols = resolved.Select(r => r.Symbol!).ToList();
        symbols.ShouldContain(new DamlSymbolId(package, "Names", "Point'", null, DamlSymbolKind.Record));
        symbols.ShouldContain(new DamlSymbolId(package, "Names", "Point_u0027", null, DamlSymbolKind.Record));
        symbols.ShouldContain(new DamlSymbolId(package, "Names", "Point'", "x'", DamlSymbolKind.Field));
        symbols.ShouldContain(new DamlSymbolId(package, "Names", "Mode'", "One'", DamlSymbolKind.Constructor));
        symbols.ShouldContain(new DamlSymbolId(package, "Names", "Result'", "Value'", DamlSymbolKind.Constructor));
        symbols.ShouldContain(new DamlSymbolId(package, "Names.More", "Asset'", "Read'", DamlSymbolKind.Choice));
        symbols.ShouldContain(new DamlSymbolId(package, "Names'.Types", "Entry'", null, DamlSymbolKind.Record));
    }

    // The Daml names a declaration states, and the C# declaration that states them.
    private static (DamlSymbolKind, string?, string?, string, string?, string) Row(GeneratedDeclaration d) =>
        (d.Name.Kind, d.Name.PackageId, d.Name.Module, d.Name.Entity, d.Name.Member, d.DeclarationId);

    [Fact, Trait("Category", "Unit")]
    public void The_fixture_compiles_as_a_project_with_the_Daml_runtime_does()
    {
        var compilation = GeneratedCode.Compile(GeneratedCode.Files(GeneratedCode.Kinds));

        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_template_brings_its_identity_fields_and_choices_with_their_argument_records()
    {
        Kinds.Where(d => d.Name.Entity == "Asset").Select(Row).ShouldBe(
        [
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "issuer", "P:Fixture.Kinds.Asset.Asset.Issuer"),
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "owner", "P:Fixture.Kinds.Asset.Asset.Owner"),
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "name", "P:Fixture.Kinds.Asset.Asset.Name"),
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "position", "P:Fixture.Kinds.Asset.Asset.Position"),
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "shape", "P:Fixture.Kinds.Asset.Asset.Shape"),
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "colour", "P:Fixture.Kinds.Asset.Asset.Colour"),
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "tags", "P:Fixture.Kinds.Asset.Asset.Tags"),
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "amount", "P:Fixture.Kinds.Asset.Asset.Amount"),
            (DamlSymbolKind.Template, Package, "Kinds.Asset", "Asset", null, "T:Fixture.Kinds.Asset.Asset"),
            (DamlSymbolKind.Choice, Package, "Kinds.Asset", "Asset", "Archive", "P:Fixture.Kinds.Asset.Asset.ChoiceArchive"),
            (DamlSymbolKind.Choice, Package, "Kinds.Asset", "Asset", "Give", "P:Fixture.Kinds.Asset.Asset.ChoiceGive"),
            (DamlSymbolKind.Choice, Package, "Kinds.Asset", "Asset", "Describe", "P:Fixture.Kinds.Asset.Asset.ChoiceDescribe"),
            (DamlSymbolKind.Choice, Package, "Kinds.Asset", "Asset", "Describe", "T:Fixture.Kinds.Asset.Asset.Describe"),
            (DamlSymbolKind.Choice, Package, "Kinds.Asset", "Asset", "Give", "T:Fixture.Kinds.Asset.Asset.Give"),
            (DamlSymbolKind.Field, Package, "Kinds.Asset", "Asset", "Give.newOwner", "P:Fixture.Kinds.Asset.Asset.Give.NewOwner"),
        ],
        ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_template_of_the_module_is_found_with_its_own_identity()
    {
        Kinds.Where(d => d.Name.Kind == DamlSymbolKind.Template).Select(d => (d.Name.Module, d.Name.Entity, d.DeclarationId)).ShouldBe(
            [("Kinds.Asset", "Asset", "T:Fixture.Kinds.Asset.Asset"), ("Kinds.Asset", "Note", "T:Fixture.Kinds.Asset.Note")],
            ignoreOrder: true);
    }

    // An interface choice's argument is a record of its own in the interface's namespace.
    [Fact, Trait("Category", "Unit")]
    public void An_interface_brings_its_identity_and_choices_and_its_view_is_a_record()
    {
        Kinds.Where(d => d.Name.Namespace == "Fixture.Kinds.Holding").Select(Row).ShouldBe(
        [
            (DamlSymbolKind.Record, null, null, "HoldingView", null, "T:Fixture.Kinds.Holding.HoldingView"),
            (DamlSymbolKind.Field, null, null, "HoldingView", "owner", "P:Fixture.Kinds.Holding.HoldingView.Owner"),
            (DamlSymbolKind.Field, null, null, "HoldingView", "amount", "P:Fixture.Kinds.Holding.HoldingView.Amount"),
            (DamlSymbolKind.Interface, Package, "Kinds.Holding", "Holding", null, "T:Fixture.Kinds.Holding.IHolding"),
            (DamlSymbolKind.Choice, Package, "Kinds.Holding", "Holding", "Archive", "P:Fixture.Kinds.Holding.IHolding.ChoiceArchive"),
            (DamlSymbolKind.Choice, Package, "Kinds.Holding", "Holding", "Transfer", "P:Fixture.Kinds.Holding.IHolding.ChoiceTransfer"),
            (DamlSymbolKind.Choice, Package, "Kinds.Holding", "Holding", "Transfer", "T:Fixture.Kinds.Holding.Transfer"),
            (DamlSymbolKind.Field, Package, "Kinds.Holding", "Holding", "Transfer.newOwner", "P:Fixture.Kinds.Holding.Transfer.NewOwner"),
        ],
        ignoreOrder: true);
    }

    // Data types don't state their package or module; the generation they belong to does.
    [Fact, Trait("Category", "Unit")]
    public void Records_variants_and_enums_bring_their_members_by_their_Daml_names()
    {
        Kinds.Where(d => d.Name.Namespace == "Fixture.Kinds.Types").Select(Row).ShouldBe(
        [
            (DamlSymbolKind.Record, null, null, "Box", null, "T:Fixture.Kinds.Types.Box`1"),
            (DamlSymbolKind.Field, null, null, "Box", "content", "P:Fixture.Kinds.Types.Box`1.Content"),
            (DamlSymbolKind.Field, null, null, "Box", "label", "P:Fixture.Kinds.Types.Box`1.Label"),
            (DamlSymbolKind.Enum, null, null, "Colour", null, "T:Fixture.Kinds.Types.Colour"),
            (DamlSymbolKind.Constructor, null, null, "Colour", "Red", "F:Fixture.Kinds.Types.Colour.Red"),
            (DamlSymbolKind.Constructor, null, null, "Colour", "Green", "F:Fixture.Kinds.Types.Colour.Green"),
            (DamlSymbolKind.Constructor, null, null, "Colour", "Blue", "F:Fixture.Kinds.Types.Colour.Blue"),
            (DamlSymbolKind.Record, null, null, "Point", null, "T:Fixture.Kinds.Types.Point"),
            (DamlSymbolKind.Field, null, null, "Point", "x", "P:Fixture.Kinds.Types.Point.X"),
            (DamlSymbolKind.Field, null, null, "Point", "y", "P:Fixture.Kinds.Types.Point.Y"),
            (DamlSymbolKind.Variant, null, null, "Shape", null, "T:Fixture.Kinds.Types.Shape"),
            (DamlSymbolKind.Constructor, null, null, "Shape", "Circle", "T:Fixture.Kinds.Types.Shape.Circle"),
            (DamlSymbolKind.Constructor, null, null, "Shape", "Square", "T:Fixture.Kinds.Types.Shape.Square"),
            (DamlSymbolKind.Constructor, null, null, "Shape", "Empty", "T:Fixture.Kinds.Types.Shape.Empty"),
            (DamlSymbolKind.Constructor, null, null, "Shape", "Circle", "T:Fixture.Kinds.Types.Shape_Circle"),
            (DamlSymbolKind.Field, null, null, "Shape", "Circle.radius", "P:Fixture.Kinds.Types.Shape_Circle.Radius"),
        ],
        ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public void Each_declaration_points_at_its_name_in_its_file()
    {
        foreach (var declaration in Kinds)
        {
            var text = File.ReadAllText(declaration.FilePath);
            var name = declaration.DeclarationId.Split('.', ':').Last().Split('`')[0];
            text.Substring(declaration.Span.Start, declaration.Span.Length).ShouldBe(name, declaration.ToString());
        }
        Kinds.Select(d => d.DeclarationId + "@" + d.FilePath).ShouldBeUnique();
    }

    // Registration and extension classes, and the Archive helpers outside the template, are plumbing.
    [Fact, Trait("Category", "Unit")]
    public void Plumbing_the_generator_writes_around_the_types_stands_for_nothing()
    {
        Kinds.ShouldNotContain(d => d.DeclarationId.Contains("PackageRegistration_")
            || d.DeclarationId.Contains("Extensions")
            || d.DeclarationId.StartsWith("M:", System.StringComparison.Ordinal));
    }

    // Reformatted, with usings instead of global:: names: the same declarations, as the code means the same.
    [Fact, Trait("Category", "Unit")]
    public void Layout_and_how_names_are_written_dont_matter()
    {
        var files = GeneratedCode.Files(GeneratedCode.Kinds).Select(f => (f.Path, Reformat(f.Text))).ToList();

        var reformatted = Extract(GeneratedCode.Compile(files));

        reformatted.Select(Row).ShouldBe(Kinds.Select(Row), ignoreOrder: true);
    }

    private static string Reformat(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text, GeneratedCode.ParseOptions).GetRoot();
        var simplified = root.ReplaceNodes(
            root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AliasQualifiedNameSyntax>(),
            (_, alias) => alias.Name.WithTriviaFrom(alias));
        var withUsings = "using Daml.Runtime.Data;\nusing Daml.Runtime.Commands;\n" + simplified.NormalizeWhitespace("\t", "\r\n").ToFullString();
        return withUsings.Replace("global::", "");
    }

    [Fact, Trait("Category", "Unit")]
    public void Hand_written_code_that_only_looks_generated_yields_nothing()
    {
        var compilation = GeneratedCode.Compile(
        [(@"C:\work\App\Lookalike.cs", """
            namespace App;
            public sealed class Identifier { public Identifier(string a, string b, string c) { } }
            public sealed record ChoiceName(string Name);
            public sealed partial record Asset(string Issuer)
            {
                public static Identifier TemplateId { get; } = new("pkg", "Main", "Asset");
                public static ChoiceName ChoiceGive { get; } = new("Give");
                public static Daml.Runtime.Data.Identifier Computed { get; } = new(Package(), "Main", "Asset");
                private static string Package() => "pkg";
            }
            public enum Colour { Red }
            public sealed record Point(int X);
            """)]);

        Extract(compilation).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_the_Daml_runtime_nothing_counts_as_generated()
    {
        Extract(GeneratedCode.Compile(GeneratedCode.Files(GeneratedCode.Kinds), withDamlRuntime: false)).ShouldBeEmpty();
    }

    // The mini demo's templates implement Splice interfaces, whose packages aren't referenced here.
    [Fact, Trait("Category", "Unit")]
    public void The_mini_demo_templates_and_choices_are_found_beside_their_Splice_interfaces()
    {
        var found = Extract(GeneratedCode.Compile(GeneratedCode.Files(GeneratedCode.MiniDemo)));

        found.Where(d => d.Name.Kind == DamlSymbolKind.Template).Select(d => d.Name.Entity)
            .ShouldBe(["Asset", "AssetTransferFactory", "AssetTransferInstruction", "Instrument"], ignoreOrder: true);
        found.Where(d => d.Name.Kind == DamlSymbolKind.Choice && d.Name.Entity == "Instrument").Select(d => d.Name.Member).Distinct()
            .ShouldBe(["Archive", "Mint", "TotalSupply"], ignoreOrder: true);
        found.ShouldAllBe(d => d.Name.Kind == DamlSymbolKind.Choice || d.Name.Kind == DamlSymbolKind.Field || d.Name.Kind == DamlSymbolKind.Template
            ? d.Name.PackageId == "1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb" && d.Name.Module == "MiniDemo.Asset"
            : d.Name.PackageId == null);
    }
}
