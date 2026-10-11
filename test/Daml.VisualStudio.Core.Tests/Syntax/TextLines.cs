using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.Syntax;

namespace Daml.VisualStudio.Core.Tests.Syntax;

// An editable list of lines standing in for an editor snapshot.
internal sealed class TextLines : ILineSource
{
    private readonly List<string> _lines;

    public TextLines(params string[] lines) => _lines = lines.ToList();

    public int LineCount => _lines.Count;

    public string GetLineText(int line) => _lines[line];

    public static TextLines Parse(string text) => new(text.Replace("\r\n", "\n").Split('\n'));

    // Replaces `count` lines starting at `first` with `replacement`, returning the edit as the cache sees it.
    public LineEdit Replace(int first, int count, params string[] replacement)
    {
        _lines.RemoveRange(first, count);
        _lines.InsertRange(first, replacement);
        var lastChanged = first + System.Math.Max(replacement.Length, 1) - 1;
        return new LineEdit(first, System.Math.Min(lastChanged, _lines.Count - 1), replacement.Length - count);
    }

    public TextLines Copy() => new(_lines.ToArray());
}
