using System;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>
/// The tokens of a line that layout looks at (no whitespace, comments or pragmas), with the
/// columns they start at. Tabs advance to the next multiple of the tab size: eight as in the
/// Haskell report, or whatever the editor shows them as.
/// </summary>
internal sealed class LayoutLine
{
    public const int DefaultTabSize = 8;

    private readonly LineTokens _line;
    private readonly int _tabSize;
    private readonly DamlToken[] _tokens;

    public LayoutLine(LineTokens line, int tabSize = DefaultTabSize)
    {
        _line = line;
        _tabSize = tabSize;
        var count = 0;
        foreach (var token in line.Tokens)
        {
            if (IsCode(token.Kind))
            {
                count++;
            }
        }
        _tokens = count == 0 ? Array.Empty<DamlToken>() : new DamlToken[count];
        var next = 0;
        foreach (var token in line.Tokens)
        {
            if (IsCode(token.Kind))
            {
                _tokens[next++] = token;
            }
        }
    }

    public int Count => _tokens.Length;

    public DamlToken this[int index] => _tokens[index];

    public string Text(int index) => _line.Text.Substring(_tokens[index].Start, _tokens[index].Length);

    public bool Has(string text)
    {
        for (var i = 0; i < _tokens.Length; i++)
        {
            if (Is(i, text))
            {
                return true;
            }
        }
        return false;
    }

    public bool Is(int index, string text) =>
        index < _tokens.Length && _tokens[index].Length == text.Length && string.CompareOrdinal(_line.Text, _tokens[index].Start, text, 0, text.Length) == 0;

    public bool IsAnyOf(int index, string[] texts)
    {
        foreach (var text in texts)
        {
            if (Is(index, text))
            {
                return true;
            }
        }
        return false;
    }

    public int Column(int index)
    {
        var offset = _tokens[index].Start;
        var text = _line.Text;
        if (text.IndexOf('\t', 0, offset) < 0)
        {
            return offset;
        }
        var column = 0;
        for (var i = 0; i < offset; i++)
        {
            column = text[i] == '\t' ? (column / _tabSize + 1) * _tabSize : column + 1;
        }
        return column;
    }

    /// <summary>The line's text between two of its tokens, with runs of whitespace made single spaces.</summary>
    public string? TextBetween(int afterIndex, int beforeIndex)
    {
        var start = _tokens[afterIndex].Start + _tokens[afterIndex].Length;
        var end = beforeIndex < _tokens.Length ? _tokens[beforeIndex].Start : _line.Text.Length;
        var words = _line.Text.Substring(start, end - start).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? null : string.Join(" ", words);
    }

    private static bool IsCode(DamlTokenKind kind) =>
        kind is not (DamlTokenKind.Whitespace or DamlTokenKind.LineComment or DamlTokenKind.DocComment or DamlTokenKind.BlockComment or DamlTokenKind.Pragma);
}
