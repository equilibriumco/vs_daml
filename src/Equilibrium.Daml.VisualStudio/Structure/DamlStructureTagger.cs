using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using Equilibrium.Daml.VisualStudio.Core.Concurrency;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Equilibrium.Daml.VisualStudio.Editing;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Equilibrium.Daml.VisualStudio.Structure;

/// <summary>
/// Hands out structure taggers for Daml buffers. Each caller gets its own tagger, but they all
/// read the one set of regions kept for the buffer, so outlining and the structure guides don't
/// each lex the file.
/// </summary>
[Export(typeof(ITaggerProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TagType(typeof(IStructureTag))]
internal sealed class DamlStructureTaggerProvider : ITaggerProvider
{
    private readonly IDamlLogger _logger;
    private readonly JoinableTaskContext _joinableTaskContext;

    [ImportingConstructor]
    public DamlStructureTaggerProvider(IDamlLogger logger, JoinableTaskContext joinableTaskContext)
    {
        _logger = logger;
        _joinableTaskContext = joinableTaskContext;
    }

    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer)
        where T : ITag =>
        typeof(T).IsAssignableFrom(typeof(IStructureTag))
            ? new DamlStructureTagger(DamlStructureRegions.For(buffer, _logger, _joinableTaskContext.Factory)) as ITagger<T>
            : null;
}

/// <summary>
/// The foldable regions of one buffer, worked out again on a thread-pool thread once the buffer
/// has been quiet for a moment. Shared by every tagger of the buffer; the last tagger to go
/// stops the timer.
/// </summary>
internal sealed class DamlStructureRegions : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private readonly ITextBuffer _buffer;
    private readonly IDamlLogger _logger;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Timer _timer;
    private readonly LatestResult<Regions> _results = new();
    private readonly object _timerGate = new();
    private readonly object _workGate = new();
    private int _users;
    private int _stopped;

    private DamlStructureRegions(ITextBuffer buffer, IDamlLogger logger, JoinableTaskFactory joinableTaskFactory)
    {
        _buffer = buffer;
        _logger = logger;
        _joinableTaskFactory = joinableTaskFactory;
        _timer = new Timer(_ => Update(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        buffer.Changed += OnBufferChanged;
    }

    /// <summary>The regions kept for <paramref name="buffer"/>, created on first use.</summary>
    public static DamlStructureRegions For(ITextBuffer buffer, IDamlLogger logger, JoinableTaskFactory joinableTaskFactory)
    {
        while (true)
        {
            var regions = buffer.Properties.GetOrCreateSingletonProperty(() => new DamlStructureRegions(buffer, logger, joinableTaskFactory));
            lock (regions._timerGate)
            {
                if (Volatile.Read(ref regions._stopped) == 0)
                {
                    regions._users++;
                    return regions;
                }
            }
        }
    }

    /// <summary>Raised on the UI thread after the regions have been worked out again.</summary>
    public event EventHandler<SnapshotSpanEventArgs>? Changed;

    /// <summary>The latest regions, with the snapshot they were computed on; null before the first pass.</summary>
    public Regions? Current => _results.Current;

    /// <summary>Releases the reference acquired by For; the last consumer stops the worker.</summary>
    public void RemoveUser()
    {
        lock (_timerGate)
        {
            if (--_users == 0) { Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_timerGate)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) { return; }
            _buffer.Changed -= OnBufferChanged;
            _results.Dispose();
            _timer.Dispose();
            _buffer.Properties.RemoveProperty(typeof(DamlStructureRegions));
        }
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        lock (_timerGate)
        {
            if (Volatile.Read(ref _stopped) == 0)
            {
                _results.Invalidate();
                _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Update()
    {
        lock (_workGate)
        {
            UpdateCurrent();
        }
    }

    private void UpdateCurrent()
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }
        try
        {
            var generation = _results.Generation;
            var snapshot = _buffer.CurrentSnapshot;
            if (_results.Current?.Snapshot == snapshot) { return; }
            var tokens = DamlTokenCache.For(_buffer).Get(snapshot);
            var specs = StructureRules.FromBlocks(LayoutAnalyzer.Analyze(tokens), tokens.Select(l => l.Text).ToList());
            var spans = specs.Select(spec => (spec, Whole(spec, snapshot))).ToList();
            var result = new Regions(snapshot, spans);
            if (!_results.TryPublish(generation, result)) { return; }
            _joinableTaskFactory.RunAsync(async () =>
            {
                // The outlining manager expects to hear about changes on the UI thread.
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                if (Volatile.Read(ref _stopped) == 0 && _results.Generation == generation && ReferenceEquals(_results.Current, result))
                {
                    Changed?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
                }
            }).FileAndForget("daml/structure/tagsChanged");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Runs on a timer thread, where an escaping exception would take Visual Studio down.
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, "Working out the folding regions failed.", ex);
        }
    }

    // The block from its first character to the end of its last line; folding may start later.
    private static SnapshotSpan Whole(StructureSpec spec, ITextSnapshot snapshot)
    {
        var first = snapshot.GetLineFromLineNumber(spec.StartLine);
        var start = first.Start + (spec.StartColumn == StructureSpec.EndOfLine ? 0 : Math.Min(spec.StartColumn, first.Length));
        return new SnapshotSpan(start, snapshot.GetLineFromLineNumber(spec.EndLine).End);
    }

    /// <summary>One pass over a snapshot: each region with the span it covers there.</summary>
    public sealed record Regions(ITextSnapshot Snapshot, IReadOnlyList<(StructureSpec Spec, SnapshotSpan Block)> Spans);
}

/// <summary>
/// Folding regions and structure guides for one consumer of a Daml buffer, mapped from the
/// buffer's shared <see cref="DamlStructureRegions"/> onto whatever snapshot is asked about.
/// </summary>
internal sealed class DamlStructureTagger : ITagger<IStructureTag>, IDisposable
{
    private const int MaxHintLength = 2000;

    private readonly DamlStructureRegions _regions;
    private int _disposed;

    public DamlStructureTagger(DamlStructureRegions regions)
    {
        _regions = regions;
        regions.Changed += OnRegionsChanged;
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _regions.Changed -= OnRegionsChanged;
            _regions.RemoveUser();
        }
    }

    public IEnumerable<ITagSpan<IStructureTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        var regions = _regions.Current;
        if (spans.Count == 0 || regions is null)
        {
            return [];
        }
        var snapshot = spans[0].Snapshot;
        if (snapshot.TextBuffer != regions.Snapshot.TextBuffer || snapshot.Version.VersionNumber < regions.Snapshot.Version.VersionNumber)
        {
            return [];
        }
        var result = new List<ITagSpan<IStructureTag>>();
        foreach (var (spec, block) in regions.Spans)
        {
            var span = block.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
            if (spans.IntersectsWith(span))
            {
                result.Add(new TagSpan<IStructureTag>(span, Tag(spec, span)));
            }
        }
        return result;
    }

    private void OnRegionsChanged(object? sender, SnapshotSpanEventArgs e) => TagsChanged?.Invoke(this, e);

    private static StructureTag Tag(StructureSpec spec, SnapshotSpan block)
    {
        var snapshot = block.Snapshot;
        var firstLine = snapshot.GetLineFromPosition(block.Start);
        var foldStart = spec.StartColumn == StructureSpec.EndOfLine ? firstLine.End : block.Start;
        var headerEnd = snapshot.GetLineFromLineNumber(Math.Min(firstLine.LineNumber + (spec.HeaderEndLine - spec.StartLine), snapshot.LineCount - 1)).End;
        var folded = new Span(foldStart, Math.Max(0, block.End - foldStart));
        return new StructureTag(
            snapshot,
            outliningSpan: folded,
            headerSpan: Span.FromBounds(block.Start, Math.Max(block.Start, headerEnd)),
            guideLineSpan: block.Span,
            guideLineHorizontalAnchor: block.Start,
            type: spec.Kind switch
            {
                StructureKind.Type => PredefinedStructureTagTypes.Type,
                StructureKind.Member => PredefinedStructureTagTypes.Member,
                _ => PredefinedStructureTagTypes.Nonstructural,
            },
            isCollapsible: true,
            isDefaultCollapsed: false,
            // Collapse to Definitions (Ctrl+M, O) folds declarations, not the blocks inside them.
            isImplementation: spec.Kind != StructureKind.Nonstructural,
            collapsedForm: spec.CollapsedForm,
            collapsedHintForm: Hint(snapshot, block.Span));
    }

    private static string Hint(ITextSnapshot snapshot, Span span) =>
        span.Length <= MaxHintLength ? snapshot.GetText(span) : snapshot.GetText(span.Start, MaxHintLength) + "…";
}
