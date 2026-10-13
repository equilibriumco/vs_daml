using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Editing;

/// <summary>Highlights the bracket that pairs with the one at the caret.</summary>
[Export(typeof(IViewTaggerProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TagType(typeof(TextMarkerTag))]
internal sealed class DamlBraceMatchingTaggerProvider : IViewTaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer)
        where T : ITag =>
        textView.TextBuffer == buffer ? new DamlCaretMarkerTagger(textView, "bracehighlight", Pair) as ITagger<T> : null;

    private static IEnumerable<TextSpan> Pair(IReadOnlyList<LineTokens> lines, int line, int column) =>
        BraceMatcher.FindPair(lines, line, column) is { } pair
            ? [new TextSpan(pair.Open.Line, pair.Open.Column, 1), new TextSpan(pair.Close.Line, pair.Close.Column, 1)]
            : [];
}

/// <summary>Highlights the other uses of the name at the caret, like references in C#.</summary>
[Export(typeof(IViewTaggerProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TagType(typeof(TextMarkerTag))]
internal sealed class DamlNameHighlightTaggerProvider : IViewTaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer)
        where T : ITag =>
        textView.TextBuffer == buffer ? new DamlCaretMarkerTagger(textView, "MarkerFormatDefinition/HighlightedReference", Occurrences.Find) as ITagger<T> : null;
}

/// <summary>
/// Marks text that depends on where the caret is. The marks are worked out once per caret
/// position and text version, from the buffer's shared tokens.
/// </summary>
internal sealed class DamlCaretMarkerTagger : ITagger<TextMarkerTag>, IDisposable
{
    private readonly ITextView _view;
    private readonly TextMarkerTag _tag;
    private readonly Func<IReadOnlyList<LineTokens>, int, int, IEnumerable<TextSpan>> _find;
    private SnapshotPoint? _computedAt;
    private List<SnapshotSpan> _marks = [];

    public DamlCaretMarkerTagger(ITextView view, string markerType, Func<IReadOnlyList<LineTokens>, int, int, IEnumerable<TextSpan>> find)
    {
        _view = view;
        _tag = new TextMarkerTag(markerType);
        _find = find;
        _view.Caret.PositionChanged += OnCaretMoved;
        _view.LayoutChanged += OnLayoutChanged;
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0)
        {
            return [];
        }
        var snapshot = spans[0].Snapshot;
        if (_view.Caret.Position.Point.GetPoint(snapshot.TextBuffer, PositionAffinity.Successor) is not { } caret || caret.Snapshot != snapshot)
        {
            return [];
        }
        if (_computedAt != caret)
        {
            _computedAt = caret;
            _marks = Compute(caret);
        }
        var result = new List<ITagSpan<TextMarkerTag>>();
        foreach (var mark in _marks)
        {
            if (spans.IntersectsWith(mark))
            {
                result.Add(new TagSpan<TextMarkerTag>(mark, _tag));
            }
        }
        return result;
    }

    public void Dispose()
    {
        _view.Caret.PositionChanged -= OnCaretMoved;
        _view.LayoutChanged -= OnLayoutChanged;
    }

    private List<SnapshotSpan> Compute(SnapshotPoint caret)
    {
        var snapshot = caret.Snapshot;
        var line = caret.GetContainingLine();
        var marks = new List<SnapshotSpan>();
        foreach (var span in _find(DamlTokenCache.For(snapshot.TextBuffer).Get(snapshot), line.LineNumber, caret.Position - line.Start.Position))
        {
            marks.Add(new SnapshotSpan(snapshot.GetLineFromLineNumber(span.Line).Start + span.Start, span.Length));
        }
        return marks;
    }

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) => Refresh(e.NewPosition.BufferPosition.Snapshot);

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        if (e.NewSnapshot != e.OldSnapshot)
        {
            Refresh(e.NewSnapshot);
        }
    }

    private void Refresh(ITextSnapshot snapshot) =>
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
}
