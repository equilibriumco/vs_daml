using System;
using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>
/// The lexed lines of a text, kept up to date across edits. After an edit the lines above it
/// stay as they are, the changed lines are lexed again, and so are the lines below them until
/// the lexer state at a line start is what it was before the edit; from there on the old lines
/// are kept too, since nothing in them can have changed. Not thread-safe: callers serialise access.
/// </summary>
public sealed class LineTokensCache
{
    private TokenLines _lines = TokenLines.Empty;
    private readonly List<DamlToken> _scratch = [];

    /// <summary>The lines as of the last <see cref="Reset"/> or <see cref="Update"/>; empty before either.</summary>
    public IReadOnlyList<LineTokens> Lines => _lines;

    /// <summary>Creates an independent cache sharing the immutable current snapshot.</summary>
    public LineTokensCache Fork() => new() { _lines = _lines };

    /// <summary>Lexes all of <paramref name="text"/> and returns its lines.</summary>
    public IReadOnlyList<LineTokens> Reset(ILineSource text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        var lines = new List<LineTokens>(text.LineCount);
        var state = default(LexState);
        for (var line = 0; line < text.LineCount; line++)
        {
            var lexed = Lex(text.GetLineText(line), state);
            lines.Add(lexed);
            state = lexed.EndState;
        }
        _lines = TokenLines.From(lines);
        return _lines;
    }

    /// <summary>
    /// Brings the lines up to date with <paramref name="text"/>, which has had <paramref name="edit"/>
    /// applied to the text seen last, and returns the lines that were lexed again. A text that
    /// doesn't fit the edit is lexed from the top.
    /// </summary>
    public LineRange Update(ILineSource text, LineEdit edit)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        var old = _lines;
        if (old.Count == 0 || edit.FirstLine < 0 || edit.FirstLine > old.Count || text.LineCount != old.Count + edit.LineDelta)
        {
            Reset(text);
            return new LineRange(0, text.LineCount - 1);
        }
        if (edit.FirstLine >= text.LineCount)
        {
            // Lines were removed from the end; everything that's left is unchanged.
            _lines = old.Replace(text.LineCount, old.Count - text.LineCount, []);
            return LineRange.Empty;
        }

        var updated = new List<LineTokens>();
        var state = edit.FirstLine > 0 ? old[edit.FirstLine - 1].EndState : default;
        var last = text.LineCount - 1;
        for (var line = edit.FirstLine; line < text.LineCount; line++)
        {
            var lexed = Lex(text.GetLineText(line), state);
            updated.Add(lexed);
            state = lexed.EndState;
            if (line < edit.LastLine)
            {
                continue;
            }

            // The same line in the old text, if there is one, and the state it started with.
            var oldNext = line + 1 - edit.LineDelta;
            if (oldNext >= old.Count)
            {
                last = line;
                break;
            }
            var oldStart = oldNext > 0 ? old[oldNext - 1].EndState : default;
            if (oldStart == state)
            {
                last = line;
                break;
            }
        }
        var removed = Math.Min(old.Count - edit.FirstLine, updated.Count - edit.LineDelta);
        _lines = old.Replace(edit.FirstLine, removed, updated);
        return new LineRange(edit.FirstLine, Math.Max(edit.FirstLine, last));
    }

    private LineTokens Lex(string text, LexState start)
    {
        _scratch.Clear();
        var end = DamlLexer.LexLine(text.AsSpan(), start, _scratch);
        return new LineTokens(text, _scratch.ToArray(), end);
    }
}
