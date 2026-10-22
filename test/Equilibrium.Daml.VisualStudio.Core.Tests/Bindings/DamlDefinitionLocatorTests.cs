using System;
using System.IO;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Bindings;

// The kinds fixture's Daml sources, whose generated C# the index is tested against.
public sealed class DamlDefinitionLocatorTests : IDisposable
{
    private const string Package = "bd2aa85e017dad908dbd9c8103929df3edaa7b0961b675d3ef216644565f9cb1";

    private static readonly string Fixture = Path.Combine(Repository.Root, @"test\fixtures\bindings\kinds");
    private static readonly string Manifest = Path.Combine(Fixture, "daml.yaml");

    private readonly DamlDefinitionLocator _locator = new(new PhysicalFileSystem());
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-locator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Module(string module) => Path.Combine(Fixture, "daml", module.Replace('.', '\\') + ".daml");

    // Lines and columns from zero, at the name the symbol is declared by.
    private DamlDefinition Found(DamlSymbolKind kind, string module, string entity, string? member = null) =>
        _locator.Find(new DamlSymbolId(Package, module, entity, member, kind), Manifest).Definition.ShouldNotBeNull($"{kind} {entity}.{member}");

    [Theory, Trait("Category", "Unit")]
    [InlineData(DamlSymbolKind.Template, "Kinds.Asset", "Asset", null, 5, 9)]
    [InlineData(DamlSymbolKind.Template, "Kinds.Asset", "Note", null, 31, 9)]
    [InlineData(DamlSymbolKind.Interface, "Kinds.Holding", "Holding", null, 8, 10)]
    [InlineData(DamlSymbolKind.Record, "Kinds.Types", "Point", null, 3, 5)]
    [InlineData(DamlSymbolKind.Record, "Kinds.Types", "Box", null, 20, 5)]
    [InlineData(DamlSymbolKind.Record, "Kinds.Holding", "HoldingView", null, 2, 5)]
    [InlineData(DamlSymbolKind.Variant, "Kinds.Types", "Shape", null, 9, 5)]
    [InlineData(DamlSymbolKind.Enum, "Kinds.Types", "Colour", null, 16, 5)]
    public void An_entity_is_where_its_declaration_names_it(DamlSymbolKind kind, string module, string entity, string? member, int line, int column)
    {
        var definition = Found(kind, module, entity, member);

        (definition.FilePath, definition.Line, definition.Column).ShouldBe((Module(module), line, column));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Asset", "Give", 21, 11)]
    [InlineData("Asset", "Describe", 27, 24)]
    public void A_choice_is_its_declaration_inside_the_template(string entity, string choice, int line, int column) =>
        (Found(DamlSymbolKind.Choice, "Kinds.Asset", entity, choice) is var d ? (d.Line, d.Column) : default).ShouldBe((line, column));

    [Fact, Trait("Category", "Unit")]
    public void An_interface_choice_is_its_declaration_inside_the_interface() =>
        (Found(DamlSymbolKind.Choice, "Kinds.Holding", "Holding", "Transfer") is var d ? (d.Line, d.Column) : default).ShouldBe((11, 9));

    // Archive comes with every template without being written: the template stands for it.
    [Fact, Trait("Category", "Unit")]
    public void The_implicit_Archive_choice_is_the_template() =>
        (Found(DamlSymbolKind.Choice, "Kinds.Asset", "Note", "Archive") is var d ? (d.Line, d.Column) : default).ShouldBe((31, 9));

    [Theory, Trait("Category", "Unit")]
    [InlineData(DamlSymbolKind.Field, "Kinds.Asset", "Asset", "issuer", 7, 4)]
    [InlineData(DamlSymbolKind.Field, "Kinds.Asset", "Asset", "amount", 14, 4)]
    [InlineData(DamlSymbolKind.Field, "Kinds.Asset", "Asset", "Give.newOwner", 23, 8)]
    [InlineData(DamlSymbolKind.Field, "Kinds.Holding", "Holding", "Transfer.newOwner", 13, 6)]
    [InlineData(DamlSymbolKind.Field, "Kinds.Types", "Point", "y", 5, 4)]
    [InlineData(DamlSymbolKind.Field, "Kinds.Types", "Box", "label", 22, 4)]
    [InlineData(DamlSymbolKind.Field, "Kinds.Holding", "HoldingView", "owner", 3, 4)]
    [InlineData(DamlSymbolKind.Field, "Kinds.Types", "Shape", "Circle.radius", 10, 16)]
    [InlineData(DamlSymbolKind.Constructor, "Kinds.Types", "Shape", "Circle", 10, 4)]
    [InlineData(DamlSymbolKind.Constructor, "Kinds.Types", "Shape", "Empty", 12, 4)]
    [InlineData(DamlSymbolKind.Constructor, "Kinds.Types", "Colour", "Blue", 16, 28)]
    public void A_field_or_constructor_is_its_line_in_the_declaration(DamlSymbolKind kind, string module, string entity, string member, int line, int column) =>
        (Found(kind, module, entity, member) is var d ? (d.Line, d.Column) : default).ShouldBe((line, column));

    [Fact, Trait("Category", "Unit")]
    public void A_module_the_package_doesnt_have_is_explained()
    {
        var lookup = _locator.Find(new DamlSymbolId(Package, "Kinds.Gone", "Asset", null, DamlSymbolKind.Template), Manifest);

        lookup.Definition.ShouldBeNull();
        lookup.Problem.ShouldNotBeNull().ShouldContain("Kinds.Gone");
    }

    // The sources changed after the build in a way the fingerprint check can't see yet, or never matched.
    [Fact, Trait("Category", "Unit")]
    public void A_declaration_the_module_doesnt_have_is_explained()
    {
        var lookup = _locator.Find(new DamlSymbolId(Package, "Kinds.Asset", "Asset", "Burn", DamlSymbolKind.Choice), Manifest);

        lookup.Definition.ShouldBeNull();
        lookup.Problem.ShouldNotBeNull().ShouldContain("Burn");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_package_that_cant_be_read_is_explained()
    {
        var lookup = _locator.Find(new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template), Path.Combine(_root, "daml.yaml"));

        lookup.Definition.ShouldBeNull();
        lookup.Problem.ShouldNotBeNull();
    }

    // A field written with others on one line, as Daml allows: `a, b : Party`.
    [Fact, Trait("Category", "Unit")]
    public void Fields_declared_together_are_each_found()
    {
        Directory.CreateDirectory(Path.Combine(_root, "daml"));
        File.WriteAllText(Path.Combine(_root, "daml.yaml"), "sdk-version: 3.6.0\nname: pair\nversion: 1.0.0\nsource: daml\n");
        File.WriteAllText(Path.Combine(_root, @"daml\Pair.daml"), "module Pair where\n\ntemplate Pair\n  with\n    left, right : Party\n  where\n    signatory left\n");

        var right = _locator.Find(new DamlSymbolId(Package, "Pair", "Pair", "right", DamlSymbolKind.Field), Path.Combine(_root, "daml.yaml")).Definition.ShouldNotBeNull();

        (right.Line, right.Column).ShouldBe((4, 10));
    }
}
