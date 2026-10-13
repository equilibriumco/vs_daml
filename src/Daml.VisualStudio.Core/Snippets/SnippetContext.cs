using System;
using System.Collections.Generic;
using Daml.VisualStudio.Core.Syntax;

namespace Daml.VisualStudio.Core.Snippets;

/// <summary>Where completion may offer snippets.</summary>
public static class SnippetContext
{
    /// <summary>
    /// The word snippets would replace when the caret is at <paramref name="column"/>: the line's
    /// first word, or the empty span at the caret on a line with nothing else on it. Null after
    /// other code and inside comments and strings, where every snippet would be out of place.
    /// </summary>
    /// <returns>Start and length within the line, or null.</returns>
    public static (int Start, int Length)? ApplicableSpan(IReadOnlyList<LineTokens> lines, int line, int column)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }
        if (line < 0 || line >= lines.Count)
        {
            return null;
        }
        var startsInComment = line > 0 && lines[line - 1].EndState.Mode != LexMode.Normal;
        if (startsInComment)
        {
            return null;
        }
        var text = lines[line].Text;
        column = Math.Min(Math.Max(column, 0), text.Length);
        DamlToken? first = null;
        foreach (var token in lines[line].Tokens)
        {
            if (token.Kind != DamlTokenKind.Whitespace)
            {
                first = token;
                break;
            }
        }
        if (first is not { } word)
        {
            return (column, 0);
        }
        var isWord = word.Kind is DamlTokenKind.VarId or DamlTokenKind.Keyword or DamlTokenKind.DeclarationKeyword
            or DamlTokenKind.ContractKeyword or DamlTokenKind.ChoiceModifier or DamlTokenKind.ControlKeyword or DamlTokenKind.ModuleKeyword
            or DamlTokenKind.ImportKeyword;
        if (!isWord || column < word.Start || column > word.Start + word.Length)
        {
            return null;
        }
        // Nothing but spaces may follow the word up to the caret's line end, e.g. `templ‸` but not `x = ‸`.
        foreach (var token in lines[line].Tokens)
        {
            if (token.Start > word.Start && token.Kind != DamlTokenKind.Whitespace)
            {
                return null;
            }
        }
        return (word.Start, word.Length);
    }
}
