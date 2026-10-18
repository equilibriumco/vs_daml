using System;
using Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Bindings;

public class DamlSymbolIdTests
{
    private const string Package = "bd2aa85e017dad908dbd9c8103929df3edaa7b0961b675d3ef216644565f9cb1";
    private const string Other = "1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb";

    [Fact, Trait("Category", "Unit")]
    public void It_reads_like_a_Daml_qualified_name_with_its_kind()
    {
        new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template).ToString()
            .ShouldBe($"Template {Package}:Kinds.Asset:Asset");
        new DamlSymbolId(Package, "Kinds.Asset", "Asset", "Give", DamlSymbolKind.Choice).ToString()
            .ShouldBe($"Choice {Package}:Kinds.Asset:Asset.Give");
    }

    // Module and entity alone aren't unique: two packages may both have Kinds.Asset:Asset.
    [Fact, Trait("Category", "Unit")]
    public void The_same_module_and_entity_in_two_packages_are_two_symbols()
    {
        var first = new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template);

        first.ShouldBe(new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template));
        first.ShouldNotBe(new DamlSymbolId(Other, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template));
        first.ShouldNotBe(new DamlSymbolId(Package, "Kinds.Asset", "Asset", "Give", DamlSymbolKind.Choice));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("", "Kinds.Asset", "Asset")]
    [InlineData(Package, " ", "Asset")]
    [InlineData(Package, "Kinds.Asset", "")]
    public void Package_module_and_entity_are_required(string package, string module, string entity) =>
        Should.Throw<ArgumentException>(() => new DamlSymbolId(package, module, entity, null, DamlSymbolKind.Record));

    // Choices, fields and constructors live in an entity; templates, interfaces and data types are the entity.
    [Theory, Trait("Category", "Unit")]
    [InlineData(DamlSymbolKind.Choice, null)]
    [InlineData(DamlSymbolKind.Field, "")]
    [InlineData(DamlSymbolKind.Constructor, null)]
    [InlineData(DamlSymbolKind.Template, "Give")]
    [InlineData(DamlSymbolKind.Interface, "Transfer")]
    [InlineData(DamlSymbolKind.Record, "x")]
    [InlineData(DamlSymbolKind.Variant, "Circle")]
    [InlineData(DamlSymbolKind.Enum, "Red")]
    public void A_member_is_there_exactly_for_member_kinds(DamlSymbolKind kind, string? member) =>
        Should.Throw<ArgumentException>(() => new DamlSymbolId(Package, "Kinds.Asset", "Asset", member, kind));
}
