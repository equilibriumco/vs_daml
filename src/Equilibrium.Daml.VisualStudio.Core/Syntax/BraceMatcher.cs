using System;
using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>A place in a text, by line and column (UTF-16 offset in the line), both from zero.</summary>
public readonly record struct TextPosition(int Line, int Column);

/// <summary>An opening bracket and the one that closes it.</summary>
public readonly record struct BracePair(TextPosition Open, TextPosition Close);

/// <summary>Finds the bracket that pairs with the one at the caret, going by tokens.</summary>
public static class BraceMatcher
{
    // How far to look for the partner; beyond that the pair isn't worth the time it takes.
    private const int MaxLines = 2000;

    /// <summary>
    /// The pair for the bracket right after the caret, or else right before it; null when neither
    /// is a bracket or its partner isn't found. Brackets in strings, characters and comments
    /// don't count, and only brackets of the same kind pair up.
    /// </summary>
    public static BracePair? FindPair(IReadOnlyList<LineTokens> lines, int line, int column)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }
        if (line < 0 || line >= lines.Count)
        {
            return null;
        }
        return PairAt(lines, line, column) ?? (column > 0 ? PairAt(lines, line, column - 1) : null);
    }

    private static BracePair? PairAt(IReadOnlyList<LineTokens> lines, int line, int column)
    {
        var tokens = lines[line].Tokens;
        var index = IndexAt(tokens, column);
        if (index < 0)
        {
            return null;
        }
        var token = tokens[index];
        if (token.Kind == DamlTokenKind.Unit)
        {
            return new BracePair(new TextPosition(line, token.Start), new TextPosition(line, token.Start + 1));
        }
        if (token.Kind != DamlTokenKind.Punctuation || token.Length != 1)
        {
            return null;
        }
        var bracket = lines[line].Text[token.Start];
        var here = new TextPosition(line, token.Start);
        switch (bracket)
        {
            case '(' or '[' or '{':
                return Forward(lines, line, index, bracket, Closing(bracket)) is { } close ? new BracePair(here, close) : null;
            case ')' or ']' or '}':
                return Backward(lines, line, index, Opening(bracket), bracket) is { } open ? new BracePair(open, here) : null;
            default:
                return null;
        }
    }

    private static TextPosition? Forward(IReadOnlyList<LineTokens> lines, int line, int index, char open, char close)
    {
        var depth = 0;
        for (var number = line; number < lines.Count && number <= line + MaxLines; number++)
        {
            var tokens = lines[number].Tokens;
            for (var i = number == line ? index : 0; i < tokens.Count; i++)
            {
                switch (Bracket(lines[number], tokens[i]))
                {
                    case var c when c == open:
                        depth++;
                        break;
                    case var c when c == close:
                        if (--depth == 0)
                        {
                            return new TextPosition(number, tokens[i].Start);
                        }
                        break;
                }
            }
        }
        return null;
    }

    private static TextPosition? Backward(IReadOnlyList<LineTokens> lines, int line, int index, char open, char close)
    {
        var depth = 0;
        for (var number = line; number >= 0 && number >= line - MaxLines; number--)
        {
            var tokens = lines[number].Tokens;
            for (var i = number == line ? index : tokens.Count - 1; i >= 0; i--)
            {
                switch (Bracket(lines[number], tokens[i]))
                {
                    case var c when c == close:
                        depth++;
                        break;
                    case var c when c == open:
                        if (--depth == 0)
                        {
                            return new TextPosition(number, tokens[i].Start);
                        }
                        break;
                }
            }
        }
        return null;
    }

    // The bracket a token is, or '\0' for anything else.
    private static char Bracket(LineTokens line, DamlToken token) =>
        token.Kind == DamlTokenKind.Punctuation && token.Length == 1 ? line.Text[token.Start] : '\0';

    // The token covering the character at `column`.
    private static int IndexAt(IReadOnlyList<DamlToken> tokens, int column)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Start <= column && column < tokens[i].Start + tokens[i].Length)
            {
                return i;
            }
        }
        return -1;
    }

    private static char Closing(char open) => open switch { '(' => ')', '[' => ']', _ => '}' };

    private static char Opening(char close) => close switch { ')' => '(', ']' => '[', _ => '{' };
}
