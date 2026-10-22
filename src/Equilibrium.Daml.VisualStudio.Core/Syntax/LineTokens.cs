using System;
using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>One line of text with the tokens the lexer found on it.</summary>
/// <param name="Text">The line's text, without the line break.</param>
/// <param name="Tokens">The line's tokens, covering it exactly, in order.</param>
/// <param name="EndState">The lexer state the line ends in.</param>
public sealed record LineTokens(string Text, IReadOnlyList<DamlToken> Tokens, LexState EndState)
{
    /// <summary>Lexes whole lines, carrying the lexer state from each line to the next.</summary>
    public static IReadOnlyList<LineTokens> Lex(IEnumerable<string> lines)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }
        var result = new List<LineTokens>();
        var state = default(LexState);
        foreach (var line in lines)
        {
            var tokens = new List<DamlToken>();
            state = DamlLexer.LexLine(line.AsSpan(), state, tokens);
            result.Add(new LineTokens(line, tokens, state));
        }
        return result;
    }

    /// <summary>Lexes a whole text; <c>\r\n</c> and <c>\n</c> both end a line.</summary>
    public static IReadOnlyList<LineTokens> Lex(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        return Lex(text.Replace("\r\n", "\n").Split('\n'));
    }
}
