using System.Collections.Generic;

namespace Daml.VisualStudio.Core.Syntax;

/// <summary>
/// The tokens of a line that layout looks at (no whitespace, comments or pragmas), with the
/// columns they start at. Tabs advance to the next multiple of eight, as in the Haskell report.
/// </summary>
internal sealed class LayoutLine
{
    private readonly LineTokens _line;
    private readonly List<DamlToken> _tokens = new();

    public LayoutLine(LineTokens line)
    {
        _line = line;
        foreach (var token in line.Tokens)
        {
            if (IsCode(token.Kind))
            {
                _tokens.Add(token);
            }
        }
    }

    public int Count => _tokens.Count;

    public DamlToken this[int index] => _tokens[index];

    public string Text(int index) => _line.Text.Substring(_tokens[index].Start, _tokens[index].Length);

    public bool Is(int index, string text) =>
        index < _tokens.Count && _tokens[index].Length == text.Length && string.CompareOrdinal(_line.Text, _tokens[index].Start, text, 0, text.Length) == 0;

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
            column = text[i] == '\t' ? (column / 8 + 1) * 8 : column + 1;
        }
        return column;
    }

    /// <summary>The line's text between two of its tokens, with runs of whitespace made single spaces.</summary>
    public string? TextBetween(int afterIndex, int beforeIndex)
    {
        var start = _tokens[afterIndex].Start + _tokens[afterIndex].Length;
        var end = beforeIndex < _tokens.Count ? _tokens[beforeIndex].Start : _line.Text.Length;
        var words = _line.Text.Substring(start, end - start).Split([' ', '\t'], System.StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? null : string.Join(" ", words);
    }

    private static bool IsCode(DamlTokenKind kind) =>
        kind is not (DamlTokenKind.Whitespace or DamlTokenKind.LineComment or DamlTokenKind.DocComment or DamlTokenKind.BlockComment or DamlTokenKind.Pragma);
}
