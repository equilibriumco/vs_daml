using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Syntax;

public class StructureRulesTests
{
    private static IReadOnlyList<StructureSpec> Specs(params string[] lines) =>
        StructureRules.FromBlocks(LayoutAnalyzer.Analyze(LineTokens.Lex(lines)), lines);

    private static readonly string[] Template =
    [
        "module Main where",                     // 0
        "",                                      // 1
        "import Daml.Script",                    // 2
        "import DA.List",                        // 3
        "",                                      // 4
        "template Asset",                        // 5
        "  with",                                // 6
        "    owner : Party",                     // 7
        "  where",                               // 8
        "    signatory owner",                   // 9
        "    nonconsuming choice Transfer : ()", // 10
        "      controller owner",                // 11
        "      do",                              // 12
        "        pure ()",                       // 13
        "",                                      // 14
        "setup : Script ()",                     // 15
        "setup = script do",                     // 16
        "  pure ()",                             // 17
    ];

    [Fact, Trait("Category", "Unit")]
    public void Declarations_fold_whole_and_show_what_they_declare()
    {
        var specs = Specs(Template);

        specs.Single(s => s.StartLine == 5).ShouldBe(new StructureSpec(StructureKind.Type, 5, 0, 13, 5, "template Asset …"));
        specs.Single(s => s.StartLine == 10).ShouldBe(new StructureSpec(StructureKind.Member, 10, 4, 13, 10, "nonconsuming choice Transfer …"));
        specs.Single(s => s.StartLine == 15).ShouldBe(new StructureSpec(StructureKind.Member, 15, 0, 17, 15, "setup …"));
    }

    [Fact, Trait("Category", "Unit")]
    public void Imports_fold_together_and_say_how_many_there_are()
    {
        Specs(Template).Single(s => s.StartLine == 2).ShouldBe(new StructureSpec(StructureKind.Nonstructural, 2, 0, 3, 2, "import … (2)"));
    }

    [Fact, Trait("Category", "Unit")]
    public void Layout_blocks_fold_after_their_first_line()
    {
        var specs = Specs(Template);

        specs.Single(s => s.StartLine == 8).ShouldBe(new StructureSpec(StructureKind.Nonstructural, 8, StructureSpec.EndOfLine, 13, 8, "…"));
    }

    [Fact, Trait("Category", "Unit")]
    public void Do_and_with_blocks_shorter_than_three_lines_dont_fold()
    {
        var specs = Specs(Template);

        // `with owner : Party`, the choice's `do` and `script do` are two lines each.
        specs.ShouldNotContain(s => s.StartLine == 6);
        specs.ShouldNotContain(s => s.StartLine == 12);
        specs.ShouldNotContain(s => s.StartLine == 16);
    }

    [Fact, Trait("Category", "Unit")]
    public void Single_lines_and_the_module_itself_dont_fold()
    {
        var specs = Specs("module M where", "x = 1", "data D = D", "{- one line -}");

        specs.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Kinds_follow_what_the_block_is()
    {
        var specs = Specs(
            "data Point = Point with", // 0
            "    x : Int",             // 1
            "    y : Int",             // 2
            "class C a where",         // 3
            "  f : a -> a",            // 4
            "instance C Int where",    // 5
            "  f x = x",               // 6
            "{- note",                 // 7
            "   more -}",              // 8
            "exception E",             // 9
            "  with",                  // 10
            "    m : Text");           // 11

        specs.Where(s => s.StartColumn == 0).Select(s => (s.StartLine, s.Kind, s.CollapsedForm)).ShouldBe(
        [
            (0, StructureKind.Type, "data Point …"),
            (3, StructureKind.Type, "class C …"),
            (5, StructureKind.Type, "instance C Int …"),
            (9, StructureKind.Type, "exception E …"),
        ]);
        specs.Single(s => s.StartLine == 7).ShouldBe(new StructureSpec(StructureKind.Nonstructural, 7, StructureSpec.EndOfLine, 8, 7, "…"));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_declaration_without_its_name_on_the_first_line_shows_that_line()
    {
        var specs = Specs(
            "instance",
            "  ( Show a",
            "  ) => Show (Pair a) where",
            "  show _ = \"\"");

        specs[0].ShouldBe(new StructureSpec(StructureKind.Type, 0, 0, 3, 2, "instance …"));
    }

    [Fact, Trait("Category", "Unit")]
    public void Specs_come_in_document_order_outer_before_inner()
    {
        var specs = Specs(Template);

        specs.Select(s => s.StartLine).ShouldBe(specs.Select(s => s.StartLine).OrderBy(l => l).ToList());
        specs.Select(s => s.StartLine).ShouldBe([2, 5, 8, 10, 15]);
    }
}
