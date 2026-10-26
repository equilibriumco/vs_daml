using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Syntax;

public class LineTokensCacheTests
{
    private static TextLines TenLines() => new(Enumerable.Range(0, 10).Select(i => $"x{i} = {i}").ToArray());

    private static LineTokensCache Lexed(TextLines text)
    {
        var cache = new LineTokensCache();
        cache.Reset(text);
        return cache;
    }

    // One line per line: its text, end state and tokens, so two lexings can be compared whole.
    private static List<string> Flat(IEnumerable<LineTokens> lines) =>
        lines.Select(l => $"{l.Text}|{l.EndState}|{string.Join(",", l.Tokens.Select(t => $"{t.Kind}@{t.Start}+{t.Length}"))}").ToList();

    private static List<string> Fresh(TextLines text) =>
        Flat(LineTokens.Lex(Enumerable.Range(0, text.LineCount).Select(text.GetLineText)));

    [Fact, Trait("Category", "Unit")]
    public void Reset_lexes_the_whole_text_like_a_one_off_lex()
    {
        var text = new TextLines("a = 1", "{- open", "still comment", "-} b = 2", "c = 3");

        var lines = new LineTokensCache().Reset(text);

        Flat(lines).ShouldBe(Fresh(text));
        lines[2].EndState.Mode.ShouldBe(LexMode.BlockComment);
    }

    // The state a line starts in is the previous line's end state; line 0 starts in the default.
    private static LexState StartOf(LineTokensCache cache, int line) => line == 0 ? default : cache.Lines[line - 1].EndState;

    [Fact, Trait("Category", "Unit")]
    public void Closing_a_comment_earlier_relexes_only_up_to_where_the_states_agree_again()
    {
        var text = TenLines();
        var cache = Lexed(text);
        cache.Update(text, text.Replace(2, 1, "x2 = 2 {- now a comment"));
        cache.Update(text, text.Replace(7, 1, "-} x7 = 7"));

        // Closed on line 5 instead: lines 5 to 7 change, and from the old closing line on the states agree.
        var range = cache.Update(text, text.Replace(5, 1, "-} x5 = 5"));

        range.ShouldBe(new LineRange(5, 7));
        Flat(cache.Lines).ShouldBe(Fresh(text));
    }

    [Fact, Trait("Category", "Unit")]
    public void Deleting_the_line_that_closed_a_comment_extends_the_comment()
    {
        var text = new TextLines("a = 1", "{- open", "-}", "b = 2", "c = 3");
        var cache = Lexed(text);

        var range = cache.Update(text, text.Replace(2, 1));

        range.ShouldBe(new LineRange(2, 3));
        StartOf(cache, 3).Mode.ShouldBe(LexMode.BlockComment);
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_line_whose_start_state_changed_is_in_the_relexed_range()
    {
        string[] pieces = ["x = 1", "{- a", "-}", "-- c", "s = \"gap\\", "\\end\""];
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var text = new TextLines(Enumerable.Range(0, 10).Select(_ => pieces[random.Next(pieces.Length)]).ToArray());
            var cache = Lexed(text);
            var before = Enumerable.Range(0, text.LineCount).Select(i => StartOf(cache, i)).ToList();
            var first = random.Next(text.LineCount);

            var range = cache.Update(text, text.Replace(first, 1, pieces[random.Next(pieces.Length)]));

            for (var line = 0; line < text.LineCount; line++)
            {
                if (StartOf(cache, line) != before[line])
                {
                    (line >= range.FirstLine && line <= range.LastLine).ShouldBeTrue($"round {round}: line {line} changed state outside {range}");
                }
            }
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void An_edit_that_keeps_the_end_state_relexes_only_its_line()
    {
        var text = TenLines();
        var cache = Lexed(text);
        var before = cache.Lines;

        var range = cache.Update(text, text.Replace(4, 1, "renamed = 4"));

        range.ShouldBe(new LineRange(4, 4));
        cache.Lines[4].Text.ShouldBe("renamed = 4");
        ReferenceEquals(cache.Lines[3], before[3]).ShouldBeTrue("the lines above are kept");
        ReferenceEquals(cache.Lines[5], before[5]).ShouldBeTrue("the lines below are kept once the state agrees");
        ReferenceEquals(cache.Lines[9], before[9]).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public void Opening_a_block_comment_relexes_every_following_line()
    {
        var text = TenLines();
        var cache = Lexed(text);
        var before = cache.Lines;

        var range = cache.Update(text, text.Replace(2, 1, "x2 = 2 {- now a comment"));

        range.ShouldBe(new LineRange(2, 9));
        Enumerable.Range(2, 8).ShouldAllBe(i => !ReferenceEquals(cache.Lines[i], before[i]));
        cache.Lines[9].EndState.Mode.ShouldBe(LexMode.BlockComment);
        Flat(cache.Lines).ShouldBe(Fresh(text));
    }

    [Fact, Trait("Category", "Unit")]
    public void Inserted_lines_are_matched_against_the_shifted_old_lines()
    {
        var text = TenLines();
        var cache = Lexed(text);
        var before = cache.Lines;

        var range = cache.Update(text, text.Replace(3, 0, "new1 = 1", "new2 = 2"));

        range.ShouldBe(new LineRange(3, 4));
        cache.Lines.Count.ShouldBe(12);
        ReferenceEquals(cache.Lines[5], before[3]).ShouldBeTrue("the old line 3 is now line 5");
        Flat(cache.Lines).ShouldBe(Fresh(text));
    }

    [Fact, Trait("Category", "Unit")]
    public void Deleting_the_last_lines_keeps_everything_above_them()
    {
        var text = TenLines();
        var cache = Lexed(text);
        var before = cache.Lines;

        var range = cache.Update(text, text.Replace(8, 2));

        range.IsEmpty.ShouldBeTrue();
        cache.Lines.Count.ShouldBe(8);
        Enumerable.Range(0, 8).ShouldAllBe(i => ReferenceEquals(cache.Lines[i], before[i]));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_text_that_doesnt_fit_the_edit_is_lexed_from_the_top()
    {
        var text = TenLines();
        var cache = new LineTokensCache();

        var range = cache.Update(text, new LineEdit(2, 2, 0));

        range.ShouldBe(new LineRange(0, 9));
        Flat(cache.Lines).ShouldBe(Fresh(text));
    }

    [Fact, Trait("Category", "Unit")]
    public void Lines_after_random_edits_match_a_fresh_pass()
    {
        string[] pieces = ["x = 1", "{- a", "-}", "{-# P", "#-}", "s = \"gap\\", "\\end\"", "-- c", "y {- z -} w", "", "  do", "\t\tf 'c'"];
        var random = new Random(20261013);
        for (var round = 0; round < 200; round++)
        {
            var text = new TextLines(Enumerable.Range(0, 12).Select(_ => pieces[random.Next(pieces.Length)]).ToArray());
            var cache = Lexed(text);
            for (var edit = 0; edit < 10; edit++)
            {
                var first = random.Next(text.LineCount + 1);
                var removed = Math.Min(random.Next(3), text.LineCount - first);
                var added = Enumerable.Range(0, random.Next(3)).Select(_ => pieces[random.Next(pieces.Length)]).ToArray();
                if (text.LineCount - removed + added.Length == 0)
                {
                    continue;
                }
                cache.Update(text, text.Replace(first, removed, added));
                Flat(cache.Lines).ShouldBe(Fresh(text), $"round {round}, edit {edit}");
            }
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void A_one_line_edit_in_a_real_file_keeps_nearly_every_line()
    {
        var path = Path.Combine(Repository.Root, "test", "fixtures", "lexer", "mini-demo-Asset.daml");
        var text = TextLines.Parse(File.ReadAllText(path));
        var cache = Lexed(text);
        var before = cache.Lines;
        var line = Enumerable.Range(0, text.LineCount).First(i => text.GetLineText(i).TrimStart().StartsWith("owner", StringComparison.Ordinal));

        var range = cache.Update(text, text.Replace(line, 1, text.GetLineText(line) + " -- edited"));

        range.ShouldBe(new LineRange(line, line));
        Enumerable.Range(0, text.LineCount).Count(i => ReferenceEquals(cache.Lines[i], before[i])).ShouldBe(text.LineCount - 1);
        Flat(cache.Lines).ShouldBe(Fresh(text));
    }
    [Fact, Trait("Category", "Unit")]
    public void Old_snapshots_remain_unchanged_after_many_insertions_deletions_and_replacements()
    {
        var text = new TextLines(Enumerable.Range(0, 5000).Select(i => $"x{i} = {i}").ToArray());
        var cache = Lexed(text);
        var original = cache.Lines;
        var expectedOriginal = Flat(original);
        for (var i = 0; i < 600; i++)
        {
            var line = (i * 79) % (text.LineCount - 2);
            cache.Update(text, text.Replace(line, i % 3, $"added{i} = {i}", "tail = 1"));
        }
        Flat(cache.Lines).ShouldBe(Fresh(text));
        Flat(original).ShouldBe(expectedOriginal);
    }

    [Fact, Trait("Category", "Unit")]
    public void Forked_readers_can_apply_different_edits_without_mutating_each_other()
    {
        var text = TenLines();
        var original = Lexed(text);
        var fork = original.Fork();
        var forkText = TenLines();
        fork.Update(forkText, forkText.Replace(2, 1, "other = 2"));
        original.Update(text, text.Replace(6, 1, "mine = 6"));
        Flat(fork.Lines).ShouldBe(Fresh(forkText));
        Flat(original.Lines).ShouldBe(Fresh(text));
        fork.Lines[6].Text.ShouldBe("x6 = 6");
        original.Lines[2].Text.ShouldBe("x2 = 2");
    }

}
