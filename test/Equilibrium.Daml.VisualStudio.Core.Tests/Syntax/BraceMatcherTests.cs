using System.Collections.Generic;
using System.Linq;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Syntax;

public class BraceMatcherTests
{
    // ‸ marks the caret; the expected pair is given as (line, column) of each bracket.
    private static BracePair? Match(params string[] lines)
    {
        var line = System.Array.FindIndex(lines, l => l.Contains('‸'));
        var column = lines[line].IndexOf('‸');
        lines[line] = lines[line].Replace("‸", "");
        return BraceMatcher.FindPair(LineTokens.Lex(lines), line, column);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("f = ‸(a b)", 4, 8)]
    [InlineData("f = (a b)‸", 4, 8)]
    [InlineData("f = (a ‸b)", null, null)]
    [InlineData("xs = ‸[1, 2, [3]]", 5, 15)]
    [InlineData("xs = [1, 2, [3‸]]", 12, 14)]
    [InlineData("r = ‸{ x = 1 }", 4, 12)]
    [InlineData("f = ‸(g (h x) y)", 4, 14)]
    [InlineData("f = (g (h x) y)‸", 4, 14)]
    public void Brackets_on_one_line_pair_up(string line, int? open, int? close)
    {
        var pair = Match(line);

        if (open is null)
        {
            pair.ShouldBeNull();
        }
        else
        {
            pair.ShouldBe(new BracePair(new TextPosition(0, open.Value), new TextPosition(0, close!.Value)));
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void A_bracket_right_after_the_caret_wins_over_one_right_before_it()
    {
        // `)‸(`: the opening bracket after the caret is matched, as in other editors.
        Match("f = (a)‸(b)").ShouldBe(new BracePair(new TextPosition(0, 7), new TextPosition(0, 9)));
    }

    [Fact, Trait("Category", "Unit")]
    public void Pairs_span_lines()
    {
        Match(
            "xs =",
            "  ‸[ 1",
            "  , 2",
            "  ]").ShouldBe(new BracePair(new TextPosition(1, 2), new TextPosition(3, 2)));
    }

    [Fact, Trait("Category", "Unit")]
    public void Brackets_in_strings_comments_and_chars_dont_count()
    {
        Match("f = ‸(\")\" -- )", "  {- ) -} ')' )").ShouldBe(new BracePair(new TextPosition(0, 4), new TextPosition(1, 14)));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_bracket_without_its_partner_matches_nothing()
    {
        Match("f = ‸(a b").ShouldBeNull();
        Match("f = a b)‸").ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Mismatched_kinds_dont_pair()
    {
        Match("f = ‸(a b]").ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Unit_is_its_own_pair()
    {
        Match("f = ‸()").ShouldBe(new BracePair(new TextPosition(0, 4), new TextPosition(0, 5)));
        Match("f = ()‸").ShouldBe(new BracePair(new TextPosition(0, 4), new TextPosition(0, 5)));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_caret_inside_a_comment_matches_nothing()
    {
        Match("-- ‸(a)").ShouldBeNull();
    }
}

public class OccurrencesTests
{
    private static IReadOnlyList<TextSpan> Find(params string[] lines)
    {
        var line = System.Array.FindIndex(lines, l => l.Contains('‸'));
        var column = lines[line].IndexOf('‸');
        lines[line] = lines[line].Replace("‸", "");
        return Occurrences.Find(LineTokens.Lex(lines), line, column);
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_use_of_the_name_under_the_caret_is_found()
    {
        Find(
            "template Asset",
            "  with",
            "    ow‸ner : Party",
            "  where",
            "    signatory owner",
            "    observer owners -- owner").ShouldBe(
            [new TextSpan(2, 4, 5), new TextSpan(4, 14, 5)]);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_caret_right_after_a_name_counts()
    {
        Find("f x = x‸ + x").Select(s => s.Start).ShouldBe([2, 6, 10]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Type_names_are_found_too_but_not_in_strings()
    {
        Find("t : ‸Asset -> Asset", "s = \"Asset\"").Select(s => (s.Line, s.Start)).ShouldBe([(0, 4), (0, 13)]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Keywords_punctuation_and_empty_space_find_nothing()
    {
        Find("temp‸late Asset").ShouldBeEmpty();
        Find("f = ‸(x)").ShouldBeEmpty();
        Find("f =   ‸  x").ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_name_with_primes_is_a_different_name()
    {
        Find("f x‸ x' = x").Select(s => s.Start).ShouldBe([2, 9]);
    }
}
