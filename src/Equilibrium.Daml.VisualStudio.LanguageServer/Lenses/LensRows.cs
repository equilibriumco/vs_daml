using System;
using System.Collections.Generic;
using System.Linq;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Lenses;

/// <summary>
/// One clickable piece of the row shown above a declaration, such as "▶ Script results".
/// </summary>
/// <param name="Line">Zero-based line of the declaration the row sits above.</param>
/// <param name="Order">Position in the row; lower comes first.</param>
/// <param name="Text">What the row shows.</param>
/// <param name="AutomationName">What a screen reader says, and the tooltip.</param>
/// <param name="Target">What a click acts on; the provider that made the segment knows its type.</param>
public sealed record LensSegment(int Line, int Order, string Text, string AutomationName, object Target)
{
    /// <summary>The top-level declaration the segment belongs to, e.g. <c>setup</c>; null if it names none.</summary>
    public string? Declaration { get; init; }
}

/// <summary>A source of segments for the lens rows of a document.</summary>
public interface ILensSegmentProvider
{
    /// <summary>Raised, on any thread, with the URI of a document whose segments changed.</summary>
    event EventHandler<string>? SegmentsChanged;

    /// <summary>The segments of <paramref name="documentUri"/> right now.</summary>
    IReadOnlyList<LensSegment> SegmentsFor(string documentUri);
}

/// <summary>
/// A source of segments that works only for what is on screen: the editor tells it which lines of
/// a document a view shows, once scrolling stops.
/// </summary>
public interface IVisibleLensProvider : ILensSegmentProvider
{
    /// <summary>A view of <paramref name="documentUri"/> shows lines <paramref name="firstLine"/> to <paramref name="lastLine"/>.</summary>
    /// <param name="documentUri">The document.</param>
    /// <param name="firstLine">The first line shown, from zero.</param>
    /// <param name="lastLine">The last line shown.</param>
    /// <param name="textVersion">The version of the text the lines are of; the same version is the same text.</param>
    /// <param name="text">The text of that version, read only when the provider needs it.</param>
    void Shown(string documentUri, int firstLine, int lastLine, int textVersion, Func<string> text);

    /// <summary>No view shows <paramref name="documentUri"/> any more: its work stops and what was kept for it goes.</summary>
    /// <param name="documentUri">The document.</param>
    void Hidden(string documentUri);
}

/// <summary>Arranges segments into rows.</summary>
public static class LensRows
{
    /// <summary>
    /// The rows of a document: its segments grouped by line and put in order. Segments on lines
    /// the document doesn't have (the text changed since they were made) and repeats are dropped.
    /// </summary>
    /// <param name="segments">Every segment of the document, from all providers.</param>
    /// <param name="lineCount">The document's line count; without it, only negative lines are dropped.</param>
    public static IReadOnlyDictionary<int, IReadOnlyList<LensSegment>> Build(IEnumerable<LensSegment> segments, int lineCount = int.MaxValue)
    {
        if (segments is null)
        {
            throw new ArgumentNullException(nameof(segments));
        }
        return segments
            .Where(s => s.Line >= 0 && s.Line < lineCount)
            .Distinct()
            .GroupBy(s => s.Line)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LensSegment>)g.OrderBy(s => s.Order).ThenBy(s => s.Text, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The row a command run at the caret means. A row sits on its declaration's equation line,
    /// one below the type signature, so the declaration whose name starts the caret's line (its
    /// signature or any of its equations) comes first; otherwise the nearest row at or above the
    /// caret, i.e. the declaration whose body the caret is in.
    /// </summary>
    /// <param name="rows">The rows of the document, as <see cref="Build"/> gives them.</param>
    /// <param name="caretLine">Zero-based line of the caret.</param>
    /// <param name="caretLineText">That line's text.</param>
    public static IReadOnlyList<LensSegment>? ForCaret(IReadOnlyDictionary<int, IReadOnlyList<LensSegment>> rows, int caretLine, string caretLineText)
    {
        if (rows is null)
        {
            throw new ArgumentNullException(nameof(rows));
        }
        if (LeadingName(caretLineText) is { } name)
        {
            foreach (var row in rows.Values)
            {
                if (row.Any(s => s.Declaration == name))
                {
                    return row;
                }
            }
        }
        var nearest = -1;
        foreach (var line in rows.Keys)
        {
            if (line <= caretLine && line > nearest)
            {
                nearest = line;
            }
        }
        return nearest >= 0 ? rows[nearest] : null;
    }

    // A top-level name at the very start of a line: `setup : …` or `setup = …`.
    private static string? LeadingName(string? text)
    {
        if (string.IsNullOrEmpty(text) || !(char.IsLower(text![0]) || text[0] == '_'))
        {
            return null;
        }
        var end = 1;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '\''))
        {
            end++;
        }
        return end == text.Length || char.IsWhiteSpace(text[end]) || text[end] is ':' or '=' ? text.Substring(0, end) : null;
    }
}

/// <summary>"▶ Script results" over every script the server lists for a document.</summary>
public sealed class ScriptLensSegments : ILensSegmentProvider
{
    /// <summary>Script results come first in a row.</summary>
    public const int Order = 0;

    private const string TitlePrefix = "Script: ";

    private readonly ScriptLensService _lenses;

    /// <summary>Creates the provider over <paramref name="lenses"/>.</summary>
    public ScriptLensSegments(ScriptLensService lenses)
    {
        _lenses = lenses ?? throw new ArgumentNullException(nameof(lenses));
        _lenses.LensesChanged += (_, e) => SegmentsChanged?.Invoke(this, e.DocumentUri);
    }

    /// <inheritdoc />
    public event EventHandler<string>? SegmentsChanged;

    /// <inheritdoc />
    public IReadOnlyList<LensSegment> SegmentsFor(string documentUri) =>
        _lenses.GetLenses(documentUri).Select(For).ToList();

    /// <summary>The segment of one lens; its target is the lens.</summary>
    public static LensSegment For(ScriptLens lens)
    {
        if (lens is null)
        {
            throw new ArgumentNullException(nameof(lens));
        }
        var name = lens.Title.StartsWith(TitlePrefix, StringComparison.Ordinal) ? lens.Title.Substring(TitlePrefix.Length) : lens.Resource.TopLevelDecl;
        return new LensSegment(lens.Range.StartLine, Order, "▶ Script results", $"Show the results of script {name}", lens) { Declaration = lens.Resource.TopLevelDecl };
    }
}
