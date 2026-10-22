using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Bindings;

// The declarations a "C#: N references" row sits above, found by the editor's own layout analysis.
public class DamlLensDeclarationsTests
{
    private const string Source = """
        module Kinds.Asset where

        import Kinds.Holding

        data Note = Note with text : Text
          deriving (Eq, Show)

        template Asset
          with
            issuer : Party
            owner : Party
          where
            signatory issuer

            choice Give : ContractId Asset
              with
                newOwner : Party
              controller owner
              do create this with owner = newOwner

            nonconsuming choice Describe : Text
              controller owner
              do pure "an asset"

        interface Holding where
          viewtype HoldingView
          choice Transfer : ContractId Holding
            with
              to : Party
            controller (view this).owner
            do pure self

        describe : Asset -> Text
        describe _ = "asset"
        """;

    [Fact, Trait("Category", "Unit")]
    public void Templates_interfaces_and_their_choices_are_declarations_with_their_lines()
    {
        var (module, declarations) = DamlLensDeclarations.Of(Source);

        module.ShouldBe("Kinds.Asset");
        declarations.ShouldBe([
            new DamlLensDeclaration(DamlSymbolKind.Template, "Asset", null, 7),
            new DamlLensDeclaration(DamlSymbolKind.Choice, "Asset", "Give", 14),
            new DamlLensDeclaration(DamlSymbolKind.Choice, "Asset", "Describe", 20),
            new DamlLensDeclaration(DamlSymbolKind.Interface, "Holding", null, 24),
            new DamlLensDeclaration(DamlSymbolKind.Choice, "Holding", "Transfer", 26),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_file_without_a_module_header_or_templates_has_none()
    {
        var (module, declarations) = DamlLensDeclarations.Of("describe : Int\ndescribe = 1\n");

        module.ShouldBeNull();
        declarations.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_declaration_names_itself_as_the_index_does()
    {
        new DamlLensDeclaration(DamlSymbolKind.Choice, "Asset", "Give", 14).Name.ShouldBe("Asset.Give");
        new DamlLensDeclaration(DamlSymbolKind.Template, "Asset", null, 7).Name.ShouldBe("Asset");
    }
}
