using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.Syntax;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Syntax;

public class LayoutAnalyzerTests
{
    private static DamlBlock Analyze(params string[] lines) => LayoutAnalyzer.Analyze(LineTokens.Lex(lines));

    private static IEnumerable<DamlBlock> All(DamlBlock block) => new[] { block }.Concat(block.Children.SelectMany(All));

    [Fact, Trait("Category", "Unit")]
    public void A_template_holds_its_fields_and_its_body()
    {
        var root = Analyze(
            "module Main where",
            "",
            "template Asset",
            "  with",
            "    owner : Party",
            "  where",
            "    signatory owner",
            "",
            "-- trailing comment");

        root.Kind.ShouldBe(DamlBlockKind.Module);
        root.Name.ShouldBe("Main");
        var template = root.Children.ShouldHaveSingleItem();
        template.ShouldBe(template with { Kind = DamlBlockKind.Template, Name = "Asset", StartLine = 2, EndLine = 6, HeaderEndLine = 2, BodyIndent = 2 });
        template.Children.Select(c => (c.Kind, c.StartLine, c.EndLine, c.BodyIndent)).ShouldBe(
        [
            (DamlBlockKind.WithBlock, 3, 4, 4),
            (DamlBlockKind.WhereBlock, 5, 6, 4),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Choices_are_found_inside_the_where_of_a_template_with_or_without_a_modifier()
    {
        var root = Analyze(
            "template T",
            "  with",
            "    p : Party",
            "  where",
            "    signatory p",
            "    choice A : ()",
            "      controller p",
            "      do pure ()",
            "    nonconsuming choice B : ()",
            "      controller p",
            "      do",
            "        pure ()");

        var choices = All(root).Where(b => b.Kind == DamlBlockKind.Choice).ToList();

        choices.Select(c => (c.Name, c.StartLine, c.EndLine)).ShouldBe([("A", 5, 7), ("B", 8, 11)]);
        choices[1].Children.ShouldHaveSingleItem().ShouldBe(choices[1].Children[0] with { Kind = DamlBlockKind.DoBlock, StartLine = 10, EndLine = 11, BodyIndent = 8 });
    }

    [Fact, Trait("Category", "Unit")]
    public void A_signature_and_its_equations_are_one_binding_even_with_a_comment_between()
    {
        var root = Analyze(
            "isZero : Int -> Bool",
            "-- note",
            "isZero 0 = True",
            "isZero _ = False",
            "other = 1");

        root.Children.Select(c => (c.Kind, c.Name, c.StartLine, c.EndLine)).ShouldBe(
        [
            (DamlBlockKind.TopLevelBinding, "isZero", 0, 3),
            (DamlBlockKind.TopLevelBinding, "other", 4, 4),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Consecutive_imports_form_one_group()
    {
        var root = Analyze(
            "module M where",
            "import A",
            "",
            "import qualified B as C",
            "  hiding (x)",
            "f = 1");

        root.Children.Select(c => (c.Kind, c.StartLine, c.EndLine)).ShouldBe(
        [
            (DamlBlockKind.ImportGroup, 1, 4),
            (DamlBlockKind.TopLevelBinding, 5, 5),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_block_opened_mid_line_takes_the_column_of_the_next_token()
    {
        var root = Analyze(
            "f = case x of A -> 1",
            "              B -> 2",
            "g = 2");

        var caseBlock = root.Children[0].Children.ShouldHaveSingleItem();
        caseBlock.ShouldBe(caseBlock with { Kind = DamlBlockKind.CaseBlock, StartLine = 0, EndLine = 1, BodyIndent = 14 });
    }

    [Fact, Trait("Category", "Unit")]
    public void A_header_runs_on_over_lines_that_continue_it()
    {
        var root = Analyze(
            "instance",
            "  ( Show a",
            "  , Eq a",
            "  ) => Show (Pair a) where",
            "  show _ = \"\"");

        var instance = root.Children.ShouldHaveSingleItem();
        instance.Kind.ShouldBe(DamlBlockKind.Instance);
        instance.HeaderEndLine.ShouldBe(3);
        instance.Children.ShouldHaveSingleItem().Kind.ShouldBe(DamlBlockKind.WhereBlock);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_multi_line_block_comment_is_a_block_and_doesnt_end_the_code_around_it()
    {
        var root = Analyze(
            "f = 1",
            "  {- a",
            "     b -}",
            "  + 2",
            "{- c",
            "-}");

        root.Children.Select(c => (c.Kind, c.StartLine, c.EndLine)).ShouldBe(
        [
            (DamlBlockKind.TopLevelBinding, 0, 3),
            (DamlBlockKind.BlockComment, 4, 5),
        ]);
        root.Children[0].Children.ShouldHaveSingleItem().ShouldBe(root.Children[0].Children[0] with { Kind = DamlBlockKind.BlockComment, StartLine = 1, EndLine = 2 });
    }

    [Fact, Trait("Category", "Unit")]
    public void An_opener_with_nothing_indented_after_it_makes_an_empty_block()
    {
        var root = Analyze(
            "template T",
            "  with",
            "    p : Party",
            "  where",
            "f = 1");

        var where = root.Children[0].Children[1];
        where.ShouldBe(where with { Kind = DamlBlockKind.WhereBlock, StartLine = 3, EndLine = 3, BodyIndent = -1 });
        root.Children[1].Name.ShouldBe("f");
    }

    [Fact, Trait("Category", "Unit")]
    public void An_empty_file_is_an_empty_module()
    {
        var root = LayoutAnalyzer.Analyze(LineTokens.Lex(""));

        root.ShouldBe(root with { Kind = DamlBlockKind.Module, Name = null, StartLine = 0, EndLine = 0 });
        root.Children.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Tabs_count_to_the_next_multiple_of_eight()
    {
        var root = Analyze(
            "f = do",
            "\tx",
            "        y");

        var block = root.Children[0].Children.ShouldHaveSingleItem();
        block.ShouldBe(block with { EndLine = 2, BodyIndent = 8 });
    }

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(LayoutAnalyzerSnapshotTests.Fixtures), MemberType = typeof(LayoutAnalyzerSnapshotTests))]
    public void Fixture_trees_are_well_formed(string fixture)
    {
        var lines = LineTokens.Lex(LayoutAnalyzerSnapshotTests.FixtureText(fixture));

        WellFormed(LayoutAnalyzer.Analyze(lines), lines.Count).ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void The_benchmark_file_gives_a_well_formed_tree_of_its_declarations()
    {
        var lines = LineTokens.Lex(System.IO.File.ReadAllText(System.IO.Path.Combine(Daml.TestHelpers.Repository.Root, "test", "fixtures", "perf", "large-5k.daml")));

        var root = LayoutAnalyzer.Analyze(lines);

        WellFormed(root, lines.Count).ShouldBeNull();
        root.Children.Count(c => c.Kind == DamlBlockKind.Template).ShouldBeGreaterThan(10);
    }

    [Property(Arbitrary = [typeof(LayoutArbitraries)], MaxTest = 500), Trait("Category", "Unit")]
    public bool Any_text_gives_a_well_formed_tree(LayoutText text)
    {
        var lines = LineTokens.Lex(text.Value);
        return WellFormed(LayoutAnalyzer.Analyze(lines), lines.Count) is null;
    }

    // Null when the tree keeps its promises, otherwise what's wrong with it.
    internal static string? WellFormed(DamlBlock root, int lineCount)
    {
        if (root.StartLine != 0 || root.EndLine > System.Math.Max(0, lineCount - 1))
        {
            return $"the module spans L{root.StartLine}-L{root.EndLine} of {lineCount} lines";
        }
        return Check(root);

        static string? Check(DamlBlock block)
        {
            if (block.EndLine < block.StartLine || block.HeaderEndLine < block.StartLine)
            {
                return $"{block.Kind} L{block.StartLine}-L{block.EndLine} header L{block.HeaderEndLine} is out of order";
            }
            DamlBlock? previous = null;
            foreach (var child in block.Children)
            {
                if (child.StartLine < block.StartLine || child.EndLine > block.EndLine)
                {
                    return $"{child.Kind} L{child.StartLine}-L{child.EndLine} sticks out of {block.Kind} L{block.StartLine}-L{block.EndLine}";
                }
                if (previous is not null && child.StartLine <= previous.EndLine)
                {
                    return $"{child.Kind} L{child.StartLine}-L{child.EndLine} overlaps {previous.Kind} L{previous.StartLine}-L{previous.EndLine}";
                }
                if (Check(child) is { } problem)
                {
                    return problem;
                }
                previous = child;
            }
            return null;
        }
    }
}

/// <summary>Random lines built from the pieces layout depends on.</summary>
public sealed record LayoutText(string Value)
{
    public override string ToString() => "\"" + Value.Replace("\n", "\\n") + "\"";
}

public static class LayoutArbitraries
{
    private static readonly string[] Pieces =
    [
        "template T", "interface I", "interface instance I for T", "exception E", "data D = D", "class C a", "instance C Int",
        "choice X : ()", "nonconsuming choice Y : ()", "import A", "module M", "f : Int", "f x", "g",
        " where", " with", " do", " of", " let", " in", " \\case", " case x", " =", " ->", " x", " (", " )", " [", " ]",
        "{-", "-}", "--", "\"", "\t", "\n", "\n", "\n  ", "\n    ", "\n      ", "\n\t",
    ];

    public static Arbitrary<LayoutText> LayoutText() =>
        Gen.ListOf(Gen.Elements(Pieces)).Select(parts => new LayoutText(string.Concat(parts))).ToArbitrary();
}
