using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Syntax;
using Shouldly;
using VerifyXunit;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Syntax;

// Each case is a fragment with the new, empty line marked by ‸ and the indent wanted there,
// with an indent size of 2 unless the name says otherwise.
public class IndentRulesTests
{
    private const string Template = """
        module Main where

        import Daml.Script

        template Asset
          with
            issuer : Party
            owner : Party
          where
            signatory issuer
            observer owner

            choice Give : ContractId Asset
              with
                newOwner : Party
              controller owner
              do
                create this with owner = newOwner
        """;

    // The template above with the empty line put in right after the line that starts with `after`.
    private static string AfterLine(string after)
    {
        var lines = Template.Replace("\r\n", "\n").Split('\n').ToList();
        var index = lines.FindIndex(l => l.StartsWith(after, System.StringComparison.Ordinal));
        index.ShouldBeGreaterThanOrEqualTo(0, after);
        lines.Insert(index + 1, "‸");
        return string.Join("\n", lines);
    }

    public static TheoryData<string, string, int?> Cases() => new()
    {
        // Writing a template from scratch.
        { "after the module header", "module Main where\n‸", 0 },
        { "after an import", AfterLine("import Daml.Script"), 0 },
        { "after a template name", AfterLine("template Asset"), 2 },
        { "after with", AfterLine("  with"), 4 },
        { "after a field", AfterLine("    issuer : Party"), 4 },
        { "after the last field", AfterLine("    owner : Party"), 4 },
        { "after where", AfterLine("  where"), 4 },
        { "after signatory", AfterLine("    signatory issuer"), 4 },
        { "after observer", AfterLine("    observer owner"), 4 },
        { "after a choice header", AfterLine("    choice Give"), 6 },
        { "after a choice's with", AfterLine("      with"), 8 },
        { "after a choice argument", AfterLine("        newOwner : Party"), 8 },
        { "after controller", AfterLine("      controller owner"), 6 },
        { "after do", AfterLine("      do"), 8 },
        { "after a statement with an inline record", AfterLine("        create this with"), 8 },
        { "template with on the same line", "template Asset with\n‸", 2 },
        { "a modified choice", "template T\n  with\n    p : Party\n  where\n    nonconsuming choice Look : ()\n‸", 6 },
        { "interface instance", "template T\n  with\n    p : Party\n  where\n    interface instance I for T where\n‸", 6 },
        { "exception name", "exception E\n‸", 2 },
        { "interface where", "interface I where\n‸", 2 },
        { "viewtype", "interface I where\n  viewtype V\n‸", 2 },

        // Functions and expressions.
        { "after an equals sign", "f x =\n‸", 2 },
        { "after a do on the definition line", "main = do\n‸", 2 },
        { "a statement on the do line sets the column", "main = do debug 1\n‸", 10 },
        { "after a statement", "main = do\n  x <- getTime\n‸", 2 },
        { "after a continued statement", "main = do\n  x <- foo\n         bar\n‸", 2 },
        { "after case of", "f x =\n  case x of\n‸", 4 },
        { "after an alternative's arrow", "f x =\n  case x of\n    None ->\n‸", 6 },
        { "after an alternative", "f x =\n  case x of\n    Some n -> n\n‸", 4 },
        { "after lambda case", "f = \\case\n‸", 2 },
        { "after then", "f = do\n  if c\n    then\n‸", 6 },
        { "after else", "f = do\n  if c\n    then pure ()\n    else\n‸", 6 },
        { "after then do", "f = do\n  if c\n    then do\n‸", 6 },
        { "after a bare let", "f =\n  let\n‸", 4 },
        { "a let binding sets the column", "f = do\n  let x = 1\n‸", 6 },
        { "after a dollar", "f = do\n  pure $\n‸", 4 },
        { "after a bind arrow", "main = do\n  cid <-\n‸", 4 },
        { "after in", "f =\n  let x = 1 in\n‸", 4 },
        { "an equation head without its equals sign yet", "f x\n‸", 2 },
        { "a guard line continues the equation", "f x\n  | x > 0 = 1\n‸", 2 },
        { "after a lambda arrow", "f = do\n  forA_ xs \\x ->\n‸", 4 },
        { "inside an open bracket", "f = do\n  submit alice (createCmd\n‸", 4 },
        { "inside an open list", "xs =\n  [ 1\n‸", 4 },
        { "after a list item", "xs =\n  [ 1\n  , 2\n‸", 2 },
        { "after a local where", "f = g\n  where\n‸", 4 },
        { "a trailing comment doesn't count", "f = do -- start\n‸", 2 },
        { "after a top-level value", "x = 1\n‸", 0 },
        { "after a data declaration", "data Colour = Red | Green\n‸", 0 },
        { "class with a context", "class (Show a) => C a where\n‸", 2 },
        { "instance", "instance C Int where\n‸", 2 },

        // Comments and edge cases.
        { "after the line that opens a block comment", "{- notes\n‸", 3 },
        { "inside a block comment", "{- notes\n   more notes\n‸", 3 },
        { "after a block comment at the top", "module M where\n{- a\n   b -}\n‸", 0 },
        { "first line of a file", "‸", 0 },
        { "after blank lines", "f = do\n  x\n\n\n‸", 2 },
    };

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(Cases))]
    public void Desired_indentation(string name, string text, int? expected)
    {
        var (lines, index) = Parse(text);

        IndentRules.GetDesiredIndentation(lines, index, 2).ShouldBe(expected, name);
    }

    [Fact, Trait("Category", "Unit")]
    public void There_are_enough_cases() => Cases().Count.ShouldBeGreaterThanOrEqualTo(40);

    [Theory, Trait("Category", "Unit")]
    [InlineData(4, 6)]
    [InlineData(2, 4)]
    public void The_indent_size_is_the_editors(int size, int expected)
    {
        var (lines, index) = Parse("f = do\n  case x of\n‸");

        IndentRules.GetDesiredIndentation(lines, index, size).ShouldBe(expected);
    }

    [Fact, Trait("Category", "Unit")]
    public void Tabs_count_to_the_next_multiple_of_eight()
    {
        var (lines, index) = Parse("f =\n\tdo\n‸");

        IndentRules.GetDesiredIndentation(lines, index, 4).ShouldBe(12);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("template-with-where")]
    [InlineData("choice-controller-new-line")]
    [InlineData("interface-viewtype")]
    [InlineData("nested-do")]
    [InlineData("case-of")]
    [InlineData("let-block")]
    public Task Indent_after_every_line_of_a_fixture(string fixture)
    {
        // An empty line put after each line in turn, with what Enter there would indent to.
        var original = LayoutAnalyzerSnapshotTests.FixtureText(fixture).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var output = new StringBuilder();
        for (var i = 0; i < original.Length; i++)
        {
            var lines = original.Take(i + 1).Concat([""]).Concat(original.Skip(i + 1)).ToList();
            var indent = IndentRules.GetDesiredIndentation(LineTokens.Lex(lines), i + 1, 2);
            output.Append(original[i]).Append('\n').Append(indent is { } n ? new string(' ', n) + "‸" : "‸ (none)").Append('\n');
        }
        return Verifier.Verify(output.ToString(), extension: "txt").UseParameters(fixture);
    }

    private static (IReadOnlyList<LineTokens> Lines, int Index) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var index = lines.IndexOf("‸");
        index.ShouldBeGreaterThanOrEqualTo(0, "the case marks its new line with ‸");
        lines[index] = "";
        return (LineTokens.Lex(lines), index);
    }

    [Fact, Trait("Category", "Unit")]
    public void Tabs_count_with_the_editors_tab_size()
    {
        var (lines, index) = Parse("f =\n\tdo\n‸");

        IndentRules.GetDesiredIndentation(lines, index, 4, tabSize: 4).ShouldBe(8);
    }
}
