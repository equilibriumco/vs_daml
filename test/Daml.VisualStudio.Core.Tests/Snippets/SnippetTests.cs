using System.Linq;
using System.Text.RegularExpressions;
using Daml.VisualStudio.Core.Snippets;
using Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Snippets;

public class SnippetTests
{
    [Fact, Trait("Category", "Unit")]
    public void The_planned_snippets_are_there_and_exceptions_arent()
    {
        DamlSnippets.All.Select(s => s.Shortcut).ShouldBe(
            ["template", "choice", "nonconsuming choice", "interface", "interface instance", "script", "module"],
            ignoreOrder: true);
        DamlSnippets.All.ShouldNotContain(s => s.Body.Contains("exception"), "exceptions are deprecated since Daml 3.3");
    }

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(Shortcuts))]
    public void Every_snippet_has_a_first_field_and_one_default_per_field(string shortcut)
    {
        var snippet = DamlSnippets.All.Single(s => s.Shortcut == shortcut);
        var placeholders = Regex.Matches(snippet.Body, @"\$\{(\d+):([^}]*)\}").Cast<Match>().ToList();

        placeholders.ShouldContain(m => m.Groups[1].Value == "1", "the caret has to have somewhere to go");
        foreach (var field in placeholders.GroupBy(m => m.Groups[1].Value))
        {
            field.Select(m => m.Groups[2].Value).Distinct().Count().ShouldBe(1, $"field {field.Key} repeats with different defaults");
        }
        snippet.Title.ShouldNotBeNullOrWhiteSpace();
        snippet.Description.ShouldNotBeNullOrWhiteSpace();
        Regex.IsMatch(snippet.Body, @"\$(?!\{|\d)").ShouldBeFalse("a stray $ would be taken for a field");
    }

    public static TheoryData<string> Shortcuts()
    {
        var data = new TheoryData<string>();
        foreach (var snippet in DamlSnippets.All)
        {
            data.Add(snippet.Shortcut);
        }
        return data;
    }

    [Fact, Trait("Category", "Unit")]
    public void Expanding_keeps_defaults_indents_later_lines_and_selects_the_first_field()
    {
        var expanded = SnippetExpander.Expand("choice ${1:Name} : ()\n  controller ${2:owner}\n  do\n    ${0:pure ()}", "    ", "\r\n");

        expanded.Text.ShouldBe("choice Name : ()\r\n      controller owner\r\n      do\r\n        pure ()");
        expanded.Selection.ShouldBe(new SnippetField(1, 7, 4));
        expanded.Fields.Select(f => (f.Number, expanded.Text.Substring(f.Start, f.Length))).ShouldBe(
            [(1, "Name"), (2, "owner"), (0, "pure ()")]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_field_used_twice_is_recorded_twice_and_the_first_one_is_selected()
    {
        var expanded = SnippetExpander.Expand("${1:test} : Script ()\n${1:test} = pure ()", "", "\n");

        expanded.Text.ShouldBe("test : Script ()\ntest = pure ()");
        expanded.Fields.Where(f => f.Number == 1).Select(f => f.Start).ShouldBe([0, 17]);
        expanded.Selection.ShouldBe(new SnippetField(1, 0, 4));
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_fields_the_caret_goes_to_the_final_stop_or_the_end()
    {
        SnippetExpander.Expand("module M where\n\n$0", "", "\n").Selection.ShouldBe(new SnippetField(0, 16, 0));
        SnippetExpander.Expand("x = 1", "", "\n").Selection.ShouldBe(new SnippetField(0, 5, 0));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("templ‸", 0, 5)]
    [InlineData("  cho‸", 2, 3)]
    [InlineData("    nonconsuming‸", 4, 12)]
    [InlineData("te‸mplate", 0, 8)]
    [InlineData("‸", 0, 0)]
    [InlineData("    ‸", 4, 0)]
    public void Snippets_are_offered_for_the_first_word_on_a_line(string line, int start, int length)
    {
        var (lines, column) = Parse(line);

        SnippetContext.ApplicableSpan(lines, 0, column).ShouldBe((start, length));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("x = templ‸")]
    [InlineData("-- templ‸")]
    [InlineData("s = \"templ‸")]
    [InlineData("x.templ‸")]
    public void Snippets_arent_offered_after_code_or_inside_comments_and_strings(string line)
    {
        var (lines, column) = Parse(line);

        SnippetContext.ApplicableSpan(lines, 0, column).ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Snippets_arent_offered_inside_a_block_comment()
    {
        var lines = LineTokens.Lex(["{- notes", "templ", "-}"]);

        SnippetContext.ApplicableSpan(lines, 1, 5).ShouldBeNull();
    }

    private static (System.Collections.Generic.IReadOnlyList<LineTokens> Lines, int Column) Parse(string line)
    {
        var column = line.IndexOf('‸');
        return (LineTokens.Lex([line.Replace("‸", "")]), column);
    }
}
