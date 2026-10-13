using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Syntax;

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
}
