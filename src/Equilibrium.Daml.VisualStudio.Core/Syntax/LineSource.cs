namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

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
