using System;
using System.Collections.Generic;
using System.Linq;

namespace Daml.VisualStudio.LanguageServer.Lenses;

/// <summary>
/// One clickable piece of the row shown above a declaration, such as "▶ Script results".
/// </summary>
/// <param name="Line">Zero-based line of the declaration the row sits above.</param>
/// <param name="Order">Position in the row; lower comes first.</param>
/// <param name="Text">What the row shows.</param>
/// <param name="AutomationName">What a screen reader says, and the tooltip.</param>
/// <param name="Target">What a click acts on; the provider that made the segment knows its type.</param>
public sealed record LensSegment(int Line, int Order, string Text, string AutomationName, object Target);

/// <summary>A source of segments for the lens rows of a document.</summary>
public interface ILensSegmentProvider
{
    /// <summary>Raised, on any thread, with the URI of a document whose segments changed.</summary>
    event EventHandler<string>? SegmentsChanged;

    /// <summary>The segments of <paramref name="documentUri"/> right now.</summary>
    IReadOnlyList<LensSegment> SegmentsFor(string documentUri);
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
        return new LensSegment(lens.Range.StartLine, Order, "▶ Script results", $"Show the results of script {name}", lens);
    }
}
