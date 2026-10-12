using System;

namespace Daml.VisualStudio.Core.Syntax;

/// <summary>A word in a line: where it starts, how long it is, and whether it is more than blank space.</summary>
/// <param name="Start">Offset in the line.</param>
/// <param name="Length">Number of characters.</param>
/// <param name="IsSignificant">False for a run of blanks and for the empty word at the end of a line.</param>
public readonly record struct DamlWord(int Start, int Length, bool IsSignificant);

/// <summary>
/// What counts as one word in Daml, for double-click and word-wise caret moves. A name keeps its
/// primes (<c>foldl'</c>, <c>x''</c>) but a leading prime is a quote; a run of operator
/// characters (<c>&lt;-</c>, <c>==</c>) is one word; the dots of a qualified name separate words;
/// anything else is a word of its own.
/// </summary>
public static class DamlWords
{
    /// <summary>The word under <paramref name="position"/> in <paramref name="line"/>.</summary>
    public static DamlWord WordAt(string line, int position)
    {
        if (line is null)
        {
            throw new ArgumentNullException(nameof(line));
        }
        if (position < 0 || position > line.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        if (position == line.Length)
        {
            return new DamlWord(position, 0, false);
        }

        var c = line[position];
        if (c is ' ' or '\t')
        {
            return Run(line, position, ch => ch is ' ' or '\t', significant: false);
        }
        if (DamlLexer.IsSymbol(c) && c != '\'')
        {
            return Run(line, position, DamlLexer.IsSymbol, significant: true);
        }
        if (DamlLexer.IsNameChar(c))
        {
            var word = Run(line, position, DamlLexer.IsNameChar, significant: true);
            var start = word.Start;
            var end = word.Start + word.Length;
            while (start < end && line[start] == '\'')
            {
                start++;
            }
            if (position >= start)
            {
                return new DamlWord(start, end - start, true);
            }
        }
        return new DamlWord(position, 1, true);
    }

    private static DamlWord Run(string line, int position, Func<char, bool> belongs, bool significant)
    {
        var start = position;
        while (start > 0 && belongs(line[start - 1]))
        {
            start--;
        }
        var end = position + 1;
        while (end < line.Length && belongs(line[end]))
        {
            end++;
        }
        return new DamlWord(start, end - start, significant);
    }
}
