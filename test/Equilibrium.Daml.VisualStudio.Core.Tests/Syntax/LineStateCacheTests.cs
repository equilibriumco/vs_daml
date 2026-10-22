using System;
using System.Collections.Generic;
using System.Linq;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Syntax;

public class LineStateCacheTests
{
    private static readonly LexState Normal = new(LexMode.Normal, 0);
    private static readonly LexState InComment = new(LexMode.BlockComment, 1);

    private static TextLines TenLines() => new(Enumerable.Range(0, 10).Select(i => $"x{i} = {i}").ToArray());

    private static LineStateCache Lexed(TextLines text)
    {
        var cache = new LineStateCache();
        cache.StateAt(text, text.LineCount - 1);
        return cache;
    }

    // What a cold start over the whole text would give.
    private static List<LexState> Fresh(TextLines text)
    {
        var cache = new LineStateCache();
        return Enumerable.Range(0, text.LineCount).Select(i => cache.StateAt(text, i)).ToList();
    }

    [Fact, Trait("Category", "Unit")]
    public void States_are_worked_out_on_demand_from_the_previous_lines()
    {
        var text = new TextLines("a = 1", "{- open", "still comment", "-} b = 2", "c = 3");
        var cache = new LineStateCache();

        cache.StateAt(text, 0).ShouldBe(Normal);
        cache.StateAt(text, 2).ShouldBe(InComment);
        cache.StateAt(text, 4).ShouldBe(Normal);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_edit_that_keeps_the_end_state_recolours_only_its_line()
    {
        var text = TenLines();
        var cache = Lexed(text);

        var range = cache.Update(text, text.Replace(4, 1, "renamed = 4"));

        range.ShouldBe(new LineRange(4, 4));
    }

    [Fact, Trait("Category", "Unit")]
    public void Opening_a_block_comment_recolours_every_following_line()
    {
        var text = TenLines();
        var cache = Lexed(text);

        var range = cache.Update(text, text.Replace(2, 1, "x2 = 2 {- now a comment"));

        range.ShouldBe(new LineRange(2, 9));
        cache.StateAt(text, 3).ShouldBe(InComment);
        cache.StateAt(text, 9).ShouldBe(InComment);
    }

    [Fact, Trait("Category", "Unit")]
    public void Closing_the_comment_again_recolours_only_up_to_where_states_agree()
    {
        var text = TenLines();
        var cache = Lexed(text);
        cache.Update(text, text.Replace(2, 1, "x2 = 2 {- now a comment"));
        cache.Update(text, text.Replace(7, 1, "-} x7 = 7"));

        // Closing it earlier, on line 5, recolours up to the old closing line, where states agree again.
        var range = cache.Update(text, text.Replace(5, 1, "-} x5 = 5"));

        range.ShouldBe(new LineRange(5, 7));
        Enumerable.Range(0, 10).Select(i => cache.StateAt(text, i)).ShouldBe(Fresh(text));
    }

    [Fact, Trait("Category", "Unit")]
    public void Inserted_lines_are_matched_against_the_shifted_old_states()
    {
        var text = TenLines();
        var cache = Lexed(text);

        var range = cache.Update(text, text.Replace(3, 0, "new1 = 1", "new2 = 2"));

        range.ShouldBe(new LineRange(3, 4));
        Enumerable.Range(0, text.LineCount).Select(i => cache.StateAt(text, i)).ShouldBe(Fresh(text));
    }

    [Fact, Trait("Category", "Unit")]
    public void Deleting_the_line_that_closed_a_comment_extends_the_comment()
    {
        var text = new TextLines("a = 1", "{- open", "-}", "b = 2", "c = 3");
        var cache = Lexed(text);

        var range = cache.Update(text, text.Replace(2, 1));

        range.ShouldBe(new LineRange(2, 3));
        cache.StateAt(text, 3).ShouldBe(InComment);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_edit_below_anything_lexed_so_far_recolours_nothing()
    {
        var text = TenLines();
        var cache = new LineStateCache();
        cache.StateAt(text, 2);

        cache.Update(text, text.Replace(6, 1, "{- far away")).ShouldBe(LineRange.Empty);
        cache.StateAt(text, 8).ShouldBe(InComment);
    }

    [Fact, Trait("Category", "Unit")]
    public void States_after_random_edits_match_a_fresh_pass()
    {
        string[] pieces = ["x = 1", "{- a", "-}", "{-# P", "#-}", "s = \"gap\\", "\\end\"", "-- c", "y {- z -} w", ""];
        var random = new Random(20261011);
        for (var round = 0; round < 200; round++)
        {
            var text = new TextLines(Enumerable.Range(0, 12).Select(_ => pieces[random.Next(pieces.Length)]).ToArray());
            var cache = new LineStateCache();
            cache.StateAt(text, random.Next(text.LineCount));
            for (var edit = 0; edit < 10; edit++)
            {
                var first = random.Next(text.LineCount);
                var removed = Math.Min(random.Next(3), text.LineCount - first - 1);
                var added = Enumerable.Range(0, random.Next(3)).Select(_ => pieces[random.Next(pieces.Length)]).ToArray();
                if (text.LineCount - removed + added.Length == 0)
                {
                    continue;
                }
                cache.Update(text, text.Replace(first, removed, added));
            }

            Enumerable.Range(0, text.LineCount).Select(i => cache.StateAt(text, i)).ShouldBe(Fresh(text), $"round {round}");
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_line_whose_start_state_changed_is_in_the_reported_range()
    {
        string[] pieces = ["x = 1", "{- a", "-}", "-- c", "s = \"gap\\", "\\end\""];
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var text = new TextLines(Enumerable.Range(0, 10).Select(_ => pieces[random.Next(pieces.Length)]).ToArray());
            var cache = Lexed(text);
            var before = Fresh(text);
            var first = random.Next(text.LineCount);
            var edit = text.Replace(first, 1, pieces[random.Next(pieces.Length)]);

            var range = cache.Update(text, edit);

            var after = Fresh(text);
            for (var line = 0; line < text.LineCount; line++)
            {
                if (after[line] != before[line])
                {
                    (line >= range.FirstLine && line <= range.LastLine).ShouldBeTrue($"round {round}: line {line} changed state outside {range}");
                }
            }
        }
    }
}
