using System;
using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.Syntax;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Syntax;

/// <summary>Random text built from the pieces the lexer has to get right.</summary>
public sealed record DamlText(string Value)
{
    public override string ToString() => "\"" + Value.Replace("\n", "\\n") + "\"";
}

/// <summary>Any UTF-16 code units at all, unpaired surrogates and control characters included.</summary>
public sealed record AnyText(string Value)
{
    public override string ToString() => string.Join(" ", Value.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
}

public static class LexerArbitraries
{
    private static readonly string[] Pieces =
    [
        "{-", "-}", "{-#", "#-}", "--", "-- |", "-->", "|--", "\"", "\\", "'", "'a'", "x'", "''", "`", "@", "()",
        " ", "  ", "\t", "\n", "\n", "x", "Foo", "DA.List.", ".", "..", "0x1F", "1_0", "2.5e-3", "template", "with",
        "->", "::", "=", "(", ")", "[", "]", ",", "ż", "∘", "\u0001",
    ];

    public static Arbitrary<DamlText> DamlText() =>
        Gen.ListOf(Gen.Elements(Pieces)).Select(parts => new DamlText(string.Concat(parts))).ToArbitrary();

    public static Arbitrary<AnyText> AnyText() =>
        Gen.ListOf(Gen.Choose(0, 0xFFFF).Select(c => (char)c)).Select(chars => new AnyText(new string(chars.ToArray()))).ToArbitrary();

    public static Arbitrary<LexState> LexState() =>
        Gen.Elements<LexState>(
            default,
            new LexState(LexMode.BlockComment, 1),
            new LexState(LexMode.BlockComment, 3),
            new LexState(LexMode.Pragma, 1),
            new LexState(LexMode.StringGap, 0)).ToArbitrary();
}

[Properties(Arbitrary = [typeof(LexerArbitraries)], MaxTest = 500)]
public class DamlLexerPropertyTests
{
    private static string[] Lines(string text) => text.Split('\n');

    private static bool CoversExactly(string line, IReadOnlyList<DamlToken> tokens)
    {
        var position = 0;
        foreach (var token in tokens)
        {
            if (token.Start != position || token.Length <= 0)
            {
                return false;
            }
            position += token.Length;
        }
        return position == line.Length;
    }

    [Property, Trait("Category", "Unit")]
    public bool Tokens_cover_each_line_without_gaps_or_overlaps(DamlText text, LexState start)
    {
        var state = start;
        foreach (var line in Lines(text.Value))
        {
            var tokens = new List<DamlToken>();
            state = DamlLexer.LexLine(line.AsSpan(), state, tokens);
            if (!CoversExactly(line, tokens))
            {
                return false;
            }
        }
        return true;
    }

    // What an editor does: keep the state at the start of every line and re-lex from any of
    // them. Starting over at any line boundary has to give the same tokens as one pass.
    [Property, Trait("Category", "Unit")]
    public bool Restarting_at_any_line_boundary_gives_the_same_tokens(DamlText text, PositiveInt seed)
    {
        var lines = Lines(text.Value);
        var startStates = new LexState[lines.Length + 1];
        var onePass = new List<DamlToken>[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            onePass[i] = new List<DamlToken>();
            startStates[i + 1] = DamlLexer.LexLine(lines[i].AsSpan(), startStates[i], onePass[i]);
        }

        var random = new Random(seed.Get);
        var line = 0;
        while (line < lines.Length)
        {
            var chunkEnd = Math.Min(lines.Length, line + 1 + random.Next(4));
            var state = startStates[line];
            for (; line < chunkEnd; line++)
            {
                var tokens = new List<DamlToken>();
                state = DamlLexer.LexLine(lines[line].AsSpan(), state, tokens);
                if (!tokens.SequenceEqual(onePass[line]) || state != startStates[line + 1])
                {
                    return false;
                }
            }
        }
        return true;
    }

    [Property(MaxTest = 1000), Trait("Category", "Unit")]
    public bool Any_text_lexes_without_throwing_and_is_covered(AnyText text, LexState start)
    {
        var tokens = new List<DamlToken>();
        DamlLexer.LexLine(text.Value.AsSpan(), start, tokens);
        return CoversExactly(text.Value, tokens);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(LexMode.BlockComment, 0)]
    [InlineData(LexMode.Pragma, -2)]
    [InlineData(LexMode.Normal, 5)]
    [InlineData((LexMode)99, 1)]
    public void A_nonsensical_start_state_is_tolerated(LexMode mode, int depth)
    {
        var tokens = new List<DamlToken>();

        DamlLexer.LexLine("x -} {- y".AsSpan(), new LexState(mode, depth), tokens);

        Assert.True(CoversExactly("x -} {- y", tokens));
    }
}
