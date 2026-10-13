using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Structure;

/// <summary>
/// Hands out structure taggers for Daml buffers. Each caller gets its own and disposes of it with
/// its view, which stops the tagger's timer.
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
        new DamlStructureTagger(buffer, _logger, _joinableTaskContext.Factory) as ITagger<T>;
}

/// <summary>
/// Folding regions and structure guides for a Daml buffer. The regions are worked out again on a
/// thread-pool thread once the buffer has been quiet for a moment, and in between the last ones
/// are mapped onto the current text.
/// </summary>
internal sealed class DamlStructureTagger : ITagger<IStructureTag>, IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);
    private const int MaxHintLength = 2000;

    private readonly ITextBuffer _buffer;
    private readonly IDamlLogger _logger;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Timer _timer;
    private Regions? _regions;
    private int _disposed;

    public DamlStructureTagger(ITextBuffer buffer, IDamlLogger logger, JoinableTaskFactory joinableTaskFactory)
    {
        _buffer = buffer;
        _logger = logger;
        _joinableTaskFactory = joinableTaskFactory;
        _timer = new Timer(_ => Update(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        buffer.Changed += OnBufferChanged;
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _buffer.Changed -= OnBufferChanged;
            _timer.Dispose();
        }
    }

    public IEnumerable<ITagSpan<IStructureTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        var regions = Volatile.Read(ref _regions);
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

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Update()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        try
        {
            var snapshot = _buffer.CurrentSnapshot;
            var lines = snapshot.Lines.Select(l => l.GetText()).ToList();
            var specs = StructureRules.FromBlocks(LayoutAnalyzer.Analyze(LineTokens.Lex(lines)), lines);
            var spans = specs.Select(spec => (spec, Whole(spec, snapshot))).ToList();
            Volatile.Write(ref _regions, new Regions(snapshot, spans));
            _joinableTaskFactory.RunAsync(async () =>
            {
                // The outlining manager expects to hear about changes on the UI thread.
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
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

    private sealed record Regions(ITextSnapshot Snapshot, IReadOnlyList<(StructureSpec Spec, SnapshotSpan Block)> Spans);
}
