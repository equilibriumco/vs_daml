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

    /// <summary>
    /// Where a snippet starting at <paramref name="column"/> of <paramref name="line"/> would go:
    /// the top level in the first column, or among the declarations of a template or interface
    /// when the nearest line above that starts further left ends the header with <c>where</c>.
    /// Anywhere else (a choice body, a function, the fields after <c>with</c>) no snippet compiles.
    /// </summary>
    /// <param name="lines">Every line of the file, lexed with the state carried from line to line.</param>
    /// <param name="line">The line the snippet would start on.</param>
    /// <param name="column">The character offset in that line where it would start.</param>
    /// <param name="tabSize">How many columns a tab advances to, as the editor shows it.</param>
    public static SnippetPlace Place(IReadOnlyList<LineTokens> lines, int line, int column, int tabSize = LayoutLine.DefaultTabSize)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }
        if (line < 0 || line >= lines.Count)
        {
            return SnippetPlace.None;
        }
        var indent = VisualColumn(lines[line].Text, column, tabSize);
        if (indent == 0)
        {
            return SnippetPlace.TopLevel;
        }
        if (Opener(lines, line, indent, tabSize) is not { } opener || !EndsWithWhere(opener.Code))
        {
            return SnippetPlace.None;
        }
        // `template T where` or `interface I where` on one line, or a `where` of its own under the header.
        var header = opener.Code.Is(0, "where") ? Opener(lines, opener.Line, opener.Code.Column(0), tabSize)?.Code : opener.Code;
        var isContract = header is not null
            && (header.Is(0, "template") || (header.Is(0, "interface") && !header.Is(1, "instance")));
        return isContract ? SnippetPlace.ContractBody : SnippetPlace.None;
    }

    // The nearest line above that has code starting left of `indent`: the line whose block `indent` is in.
    private static (int Line, LayoutLine Code)? Opener(IReadOnlyList<LineTokens> lines, int line, int indent, int tabSize)
    {
        for (var i = line - 1; i >= 0; i--)
        {
            var code = new LayoutLine(lines[i], tabSize);
            if (code.Count > 0 && code.Column(0) < indent)
            {
                return (i, code);
            }
        }
        return null;
    }

    private static bool EndsWithWhere(LayoutLine code) => code.Is(code.Count - 1, "where");

    private static int VisualColumn(string text, int offset, int tabSize)
    {
        var column = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            column = text[i] == '\t' ? (column / tabSize + 1) * tabSize : column + 1;
        }
        return column;
    }
}
