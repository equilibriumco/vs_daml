using System;
using System.Collections.Generic;

namespace Daml.VisualStudio.Core.Syntax;

/// <summary>A stretch of one line: line and start column from zero, and its length.</summary>
public readonly record struct TextSpan(int Line, int Start, int Length);

/// <summary>
/// The other places a name appears in a file, for highlighting them while the caret is on it.
/// It goes by the name alone: the same name bound in two places counts as one, which is the
/// price of not asking the language server.
/// </summary>
public static class Occurrences
{
    /// <summary>
    /// Every token with the same text and kind as the name at the caret (or right before it),
    /// in document order; empty when the caret isn't on a name.
    /// </summary>
    public static IReadOnlyList<TextSpan> Find(IReadOnlyList<LineTokens> lines, int line, int column)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }
        if (line < 0 || line >= lines.Count || NameAt(lines[line], column) is not { } name)
        {
            return [];
        }
        var text = lines[line].Text.Substring(name.Start, name.Length);
        var result = new List<TextSpan>();
        var number = 0;
        foreach (var current in lines)
        {
            foreach (var token in current.Tokens)
            {
                if (token.Kind == name.Kind && token.Length == text.Length && string.CompareOrdinal(current.Text, token.Start, text, 0, text.Length) == 0)
                {
                    result.Add(new TextSpan(number, token.Start, token.Length));
                }
            }
            number++;
        }
        return result;
    }

    // The name the caret is in or just after.
    private static DamlToken? NameAt(LineTokens line, int column)
    {
        foreach (var token in line.Tokens)
        {
            if (token.Kind is DamlTokenKind.VarId or DamlTokenKind.ConId or DamlTokenKind.QualifiedName
                && token.Start <= column && column <= token.Start + token.Length)
            {
                return token;
            }
        }
        return null;
    }
}
