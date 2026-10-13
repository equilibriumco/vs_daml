using System;
using System.Collections.Generic;

namespace Daml.VisualStudio.Core.Syntax;

/// <summary>Read access to the lines of a text, such as one editor snapshot.</summary>
public interface ILineSource
{
    /// <summary>Number of lines; at least one, since empty text has one empty line.</summary>
    int LineCount { get; }

    /// <summary>Text of <paramref name="line"/> (0-based), without the line break.</summary>
    string GetLineText(int line);
}

/// <summary>
/// A text change in line terms: lines <see cref="FirstLine"/> to <see cref="LastLine"/> of the new
/// text replaced lines starting at <see cref="FirstLine"/> of the old text, changing the line
/// count by <see cref="LineDelta"/>.
/// </summary>
/// <param name="FirstLine">First changed line; the lines above it are untouched.</param>
/// <param name="LastLine">Last changed line in the new text.</param>
/// <param name="LineDelta">New line count minus old line count.</param>
public readonly record struct LineEdit(int FirstLine, int LastLine, int LineDelta);

/// <summary>An inclusive range of lines, or <see cref="Empty"/>.</summary>
/// <param name="FirstLine">First line in the range.</param>
/// <param name="LastLine">Last line in the range; below <paramref name="FirstLine"/> means empty.</param>
public readonly record struct LineRange(int FirstLine, int LastLine)
{
    /// <summary>No lines.</summary>
    public static LineRange Empty { get; } = new(0, -1);

    /// <summary>True when the range holds no lines.</summary>
    public bool IsEmpty => LastLine < FirstLine;
}

/// <summary>
/// Remembers the lexer state at the start of each line, so any line can be lexed on its own. The
/// states are worked out lazily, top down, as far as lines are asked for. After an edit only the
/// changed lines are re-lexed, plus following lines until the state at a line start matches what
/// it was before the edit; from there on nothing can have changed.
/// </summary>
public sealed class LineStateCache
{
    // _starts[i] is the state at the start of line i, known for every i < _starts.Count.
    private List<LexState> _starts = [default];
    private readonly List<DamlToken> _discard = [];

    /// <summary>The state at the start of <paramref name="line"/> of <paramref name="text"/>.</summary>
    public LexState StateAt(ILineSource text, int line)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        if (line < 0 || line >= text.LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        while (_starts.Count <= line)
        {
            var previous = _starts.Count - 1;
            _starts.Add(LexToEnd(text, previous, _starts[previous]));
        }
        return _starts[line];
    }

    /// <summary>
    /// Brings the cache up to date with <paramref name="text"/>, which has had <paramref name="edit"/>
    /// applied, and returns the lines whose colouring may have changed. Lines below anything asked
    /// for so far are left for later and not reported, since nobody has seen them.
    /// </summary>
    public LineRange Update(ILineSource text, LineEdit edit)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        if (edit.FirstLine >= _starts.Count || edit.FirstLine >= text.LineCount)
        {
            if (_starts.Count > text.LineCount)
            {
                // Lines were deleted below the changed one; drop states for lines that are gone.
                _starts.RemoveRange(text.LineCount, _starts.Count - text.LineCount);
            }
            return LineRange.Empty;
        }

        if (edit.LineDelta == 0)
        {
            // Ordinary typing does not shift line indexes. Update the private state array in
            // place instead of copying every known line; no reader retains this list.
            for (var line = edit.FirstLine; line < text.LineCount; line++)
            {
                var end = LexToEnd(text, line, _starts[line]);
                var next = line + 1;
                if (next >= _starts.Count)
                {
                    _starts.Add(end);
                    return new LineRange(edit.FirstLine, line);
                }
                var agrees = _starts[next] == end;
                _starts[next] = end;
                if (line >= edit.LastLine && agrees) { return new LineRange(edit.FirstLine, line); }
            }
            return new LineRange(edit.FirstLine, text.LineCount - 1);
        }

        var updated = _starts.GetRange(0, edit.FirstLine + 1);
        var lastAffected = text.LineCount - 1;
        for (var line = edit.FirstLine; line < text.LineCount; line++)
        {
            var end = LexToEnd(text, line, updated[line]);
            updated.Add(end);
            if (line < edit.LastLine)
            {
                continue;
            }

            // The start of the next line in the old text, if it was ever worked out.
            var oldNext = line + 1 - edit.LineDelta;
            if (oldNext >= _starts.Count)
            {
                lastAffected = line;
                break;
            }
            if (_starts[oldNext] == end)
            {
                for (var rest = oldNext + 1; rest < _starts.Count; rest++)
                {
                    updated.Add(_starts[rest]);
                }
                lastAffected = line;
                break;
            }
        }

        _starts = updated;
        return new LineRange(edit.FirstLine, Math.Max(edit.FirstLine, Math.Min(lastAffected, text.LineCount - 1)));
    }

    /// <summary>Forgets everything; the next request starts from the top.</summary>
    public void Clear()
    {
        _starts = [default];
    }

    private LexState LexToEnd(ILineSource text, int line, LexState start)
    {
        var end = DamlLexer.LexLine(text.GetLineText(line).AsSpan(), start, _discard);
        _discard.Clear();
        return end;
    }
}
