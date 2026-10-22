using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Equilibrium.Daml.VisualStudio.Editing;

/// <summary>Highlights the bracket that pairs with the one at the caret.</summary>
[Export(typeof(IViewTaggerProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TagType(typeof(TextMarkerTag))]
internal sealed class DamlBraceMatchingTaggerProvider : IViewTaggerProvider
{
    private readonly IDamlLogger _logger;
    private readonly JoinableTaskFactory _tasks;

    [ImportingConstructor]
    public DamlBraceMatchingTaggerProvider(IDamlLogger logger, JoinableTaskContext context)
    {
        _logger = logger;
        _tasks = context.Factory;
    }

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer)
        where T : ITag =>
        textView.TextBuffer == buffer && typeof(T).IsAssignableFrom(typeof(TextMarkerTag)) ? new DamlCaretMarkerTagger(textView, "bracehighlight", Pair, _logger, _tasks) as ITagger<T> : null;

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
    private readonly IDamlLogger _logger;
    private readonly JoinableTaskFactory _tasks;

    [ImportingConstructor]
    public DamlNameHighlightTaggerProvider(IDamlLogger logger, JoinableTaskContext context)
    {
        _logger = logger;
        _tasks = context.Factory;
    }

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer)
        where T : ITag =>
        textView.TextBuffer == buffer ? new DamlCaretMarkerTagger(textView, "MarkerFormatDefinition/HighlightedReference", Occurrences.Find, _logger, _tasks) as ITagger<T> : null;
}

/// <summary>Computes caret markers after a short pause, off the UI thread, and rejects stale results.</summary>
internal sealed class DamlCaretMarkerTagger : ITagger<TextMarkerTag>, IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(75);
    private readonly ITextView _view;
    private readonly TextMarkerTag _tag;
    private readonly Func<IReadOnlyList<LineTokens>, int, int, IEnumerable<TextSpan>> _find;
    private readonly IDamlLogger _logger;
    private readonly JoinableTaskFactory _tasks;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly object _workGate = new();
    private SnapshotPoint? _requestedAt;
    private SnapshotPoint? _computedAt;
    private long _generation;
    private bool _disposed;
    private List<SnapshotSpan> _marks = [];

    public DamlCaretMarkerTagger(ITextView view, string markerType,
        Func<IReadOnlyList<LineTokens>, int, int, IEnumerable<TextSpan>> find, IDamlLogger logger, JoinableTaskFactory tasks)
    {
        _view = view;
        _tag = new TextMarkerTag(markerType);
        _find = find;
        _logger = logger;
        _tasks = tasks;
        _timer = new Timer(_ => Update(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _view.Caret.PositionChanged += OnCaretMoved;
        _view.LayoutChanged += OnLayoutChanged;
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0) { return []; }
        var snapshot = spans[0].Snapshot;
        if (_view.Caret.Position.Point.GetPoint(snapshot.TextBuffer, PositionAffinity.Successor) is not { } caret || caret.Snapshot != snapshot)
        {
            return [];
        }
        Request(caret);
        if (_computedAt != caret) { return []; }
        var result = new List<ITagSpan<TextMarkerTag>>();
        foreach (var mark in _marks)
        {
            if (spans.IntersectsWith(mark)) { result.Add(new TagSpan<TextMarkerTag>(mark, _tag)); }
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _timer.Dispose();
        }
        _view.Caret.PositionChanged -= OnCaretMoved;
        _view.LayoutChanged -= OnLayoutChanged;
        _marks = [];
    }

    private void Request(SnapshotPoint caret)
    {
        lock (_gate)
        {
            if (_disposed || _requestedAt == caret) { return; }
            _requestedAt = caret;
            _generation++;
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Update()
    {
        lock (_workGate)
        {
            try
            {
                SnapshotPoint caret;
                long generation;
                lock (_gate)
                {
                    if (_disposed || _requestedAt is not { } requested) { return; }
                    caret = requested;
                    generation = _generation;
                }
                var snapshot = caret.Snapshot;
                var line = caret.GetContainingLine();
                var marks = _find(DamlTokenCache.For(snapshot.TextBuffer).Get(snapshot), line.LineNumber, caret.Position - line.Start.Position)
                    .Select(span => new SnapshotSpan(snapshot.GetLineFromLineNumber(span.Line).Start + span.Start, span.Length)).ToList();
                _tasks.RunAsync(async () =>
                {
                    await _tasks.SwitchToMainThreadAsync();
                    lock (_gate)
                    {
                        if (_disposed || generation != _generation || _view.IsClosed
                            || _view.Caret.Position.Point.GetPoint(snapshot.TextBuffer, PositionAffinity.Successor) != caret) { return; }
                        _computedAt = caret;
                        _marks = marks;
                    }
                    Invalidate(marks);
                }).FileAndForget("daml/caret/markers");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, "Computing caret markers failed.", ex);
            }
        }
    }

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
    {
        var snapshot = e.NewPosition.BufferPosition.Snapshot;
        var before = _marks.Select(mark => mark.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive)).ToList();
        _computedAt = null;
        _marks = [];
        Request(e.NewPosition.BufferPosition);
        Invalidate(before);
    }

    private void Invalidate(IEnumerable<SnapshotSpan> marks)
    {
        // Separate spans avoid invalidating every line between distant occurrences.
        foreach (var span in new NormalizedSnapshotSpanCollection(marks))
        {
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span));
        }
    }

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        if (e.NewSnapshot != e.OldSnapshot)
        {
            _computedAt = null;
            _marks = [];
            Request(_view.Caret.Position.BufferPosition);
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.NewSnapshot, 0, e.NewSnapshot.Length)));
        }
    }
}
