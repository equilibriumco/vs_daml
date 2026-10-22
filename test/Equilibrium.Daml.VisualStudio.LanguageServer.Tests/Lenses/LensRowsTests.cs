using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Server;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Equilibrium.Daml.VisualStudio.LanguageServer.Lenses;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Lenses;

public class LensRowsTests
{
    private static LensSegment Segment(int line, int order, string text) => new(line, order, text, text, Target: text);

    [Fact, Trait("Category", "Unit")]
    public void Segments_are_grouped_by_line_and_ordered_within_it()
    {
        var rows = LensRows.Build([Segment(3, 1, "C#: 2 references"), Segment(7, 0, "▶ Script results"), Segment(3, 0, "▶ Script results")]);

        rows.Keys.ShouldBe([3, 7], ignoreOrder: true);
        rows[3].Select(s => s.Text).ShouldBe(["▶ Script results", "C#: 2 references"]);
        rows[7].ShouldHaveSingleItem();
    }

    [Fact, Trait("Category", "Unit")]
    public void Segments_with_the_same_order_keep_a_stable_textual_order()
    {
        var rows = LensRows.Build([Segment(1, 0, "b"), Segment(1, 0, "a")]);

        rows[1].Select(s => s.Text).ShouldBe(["a", "b"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Segments_outside_the_document_or_repeated_are_dropped()
    {
        var rows = LensRows.Build([Segment(-1, 0, "x"), Segment(2, 0, "y"), Segment(2, 0, "y"), Segment(9, 0, "z")], lineCount: 5);

        rows.Keys.ShouldBe([2]);
        rows[2].ShouldHaveSingleItem();
    }

    [Fact, Trait("Category", "Unit")]
    public void No_segments_no_rows()
    {
        LensRows.Build([]).ShouldBeEmpty();
    }

    // damlc puts a script's lens on its equation (`setup = script do`), the line after its
    // signature (`setup : Script AssetId`); a caret on either line means that script.
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<LensSegment>> SampleRows = LensRows.Build(
    [
        new LensSegment(23, 0, "▶ Script results", "setup", Target: "setup") { Declaration = "setup" },
        new LensSegment(40, 0, "▶ Script results", "other", Target: "other") { Declaration = "other" },
    ]);

    [Theory, Trait("Category", "Unit")]
    [InlineData(22, "setup : Script AssetId", "setup")]
    [InlineData(23, "setup = script do", "setup")]
    [InlineData(30, "  submit bob do", "setup")]
    [InlineData(39, "other : Script ()", "other")]
    [InlineData(45, "  pure ()", "other")]
    public void The_caret_means_the_script_named_on_its_line_or_the_nearest_one_above(int line, string text, string expected)
    {
        LensRows.ForCaret(SampleRows, line, text).ShouldNotBeNull().ShouldHaveSingleItem().Target.ShouldBe(expected);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(5, "template Asset")]
    [InlineData(0, "module Main where")]
    public void Above_every_script_the_caret_means_none(int line, string text)
    {
        LensRows.ForCaret(SampleRows, line, text).ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_name_that_only_starts_like_a_script_is_not_that_script()
    {
        // `setupTwo` begins with `setup` but is another declaration; the nearest row above wins.
        LensRows.ForCaret(SampleRows, 39, "setupTwo : Script ()").ShouldNotBeNull().Single().Target.ShouldBe("setup");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_script_lens_becomes_a_results_segment_on_its_declaration_line()
    {
        var lens = new ScriptLens(new LensRange(21, 0, 21, 5), "Script: setup", VirtualResourceUri.For(@"C:\work\daml\Main.daml", "setup"));

        var segment = ScriptLensSegments.For(lens);

        segment.Line.ShouldBe(21);
        segment.Text.ShouldBe("▶ Script results");
        segment.AutomationName.ShouldBe("Show the results of script setup");
        segment.Target.ShouldBe(lens);
        segment.Order.ShouldBe(ScriptLensSegments.Order);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_script_segments_follow_the_lens_service()
    {
        using var rpc = new StreamJsonRpc.JsonRpc(Stream.Null);
        var channel = new DamlRpcChannel();
        using var service = new ScriptLensService(channel, new RecordingLogger());
        var provider = new ScriptLensSegments(service);
        var raised = new List<string>();
        provider.SegmentsChanged += (_, uri) => raised.Add(uri);

        provider.SegmentsFor("file:///C:/work/daml/Main.daml").ShouldBeEmpty();
        channel.Attach(rpc, 1); // a new session clears lenses; nothing was there to clear
        raised.ShouldBeEmpty();
    }
}
