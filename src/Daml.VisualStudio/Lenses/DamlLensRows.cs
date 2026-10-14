using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageClient;
using Daml.VisualStudio.LanguageServer.Lenses;
using Microsoft.VisualStudio.Text;

namespace Daml.VisualStudio.Lenses;

/// <summary>
/// Where lens rows come from and what a click on one of their segments does. The providers are
/// made on first use, from the language client, so opening an editor doesn't start anything.
/// </summary>
[Export(typeof(DamlLensSources))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class DamlLensSources
{
    private readonly Lazy<DamlLanguageClient> _client;
    private readonly IDamlLogger _logger;
    private readonly Lazy<IReadOnlyList<ILensSegmentProvider>> _providers;

    [ImportingConstructor]
    public DamlLensSources(Lazy<DamlLanguageClient> client, IDamlLogger logger)
    {
        _client = client;
        _logger = logger;
        _providers = new Lazy<IReadOnlyList<ILensSegmentProvider>>(() => [new ScriptLensSegments(_client.Value.Host.ScriptLenses)]);
    }

    public IReadOnlyList<ILensSegmentProvider> Providers => _providers.Value;

    /// <summary>
    /// Acts on a clicked or chosen segment; the user asked, so the results window comes up. A click
    /// runs this with nobody waiting for it, so a failure is reported here rather than thrown.
    /// </summary>
    public async Task InvokeAsync(LensSegment segment, CancellationToken cancellationToken)
    {
        switch (segment.Target)
        {
            case ScriptLens lens:
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Showing the results of {lens.Title}.");
                try
                {
                    await _client.Value.VirtualResources.ShowAsync(lens.Title, lens.Resource.ToString(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, $"Couldn't show the results of {lens.Title}.", ex);
                }
                break;
            default:
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Nothing to do for the lens segment '{segment.Text}'.");
                break;
        }
    }
}

/// <summary>
/// The lens rows of one Daml buffer. Each row is pinned to the start of its declaration's line
/// with a tracking point, so typing above it carries the row along until fresh lenses arrive.
/// Shared by every view of the buffer; the last view to close lets go of the providers.
/// </summary>
internal sealed class DamlLensRows
{
    private readonly ITextBuffer _buffer;
    private readonly string? _documentUri;
    private readonly IReadOnlyList<ILensSegmentProvider> _providers;
    private Row[] _rows = [];
    private LineMap? _lineMap;
    private int _users;

    private DamlLensRows(ITextBuffer buffer, IReadOnlyList<ILensSegmentProvider> providers)
    {
        _buffer = buffer;
        _providers = providers;
        _documentUri = buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) && !string.IsNullOrEmpty(document.FilePath)
            ? new Uri(document.FilePath).AbsoluteUri
            : null;
    }

    /// <summary>Raised, on any thread, when the rows changed; views lay out their visible lines again.</summary>
    public event EventHandler? Changed;

    public static DamlLensRows For(ITextBuffer buffer, DamlLensSources sources) =>
        buffer.Properties.GetOrCreateSingletonProperty(() => new DamlLensRows(buffer, sources.Providers));

    /// <summary>The rows in effect, if this buffer already has a lens model.</summary>
    public static DamlLensRows? Existing(ITextBuffer buffer) =>
        buffer.Properties.TryGetProperty(typeof(DamlLensRows), out DamlLensRows rows) ? rows : null;

    public void AddUser()
    {
        if (Interlocked.Increment(ref _users) == 1)
        {
            foreach (var provider in _providers)
            {
                provider.SegmentsChanged += OnSegmentsChanged;
            }
            Refresh();
        }
    }

    public void RemoveUser()
    {
        if (Interlocked.Decrement(ref _users) == 0)
        {
            foreach (var provider in _providers)
            {
                provider.SegmentsChanged -= OnSegmentsChanged;
            }
            _buffer.Properties.RemoveProperty(typeof(DamlLensRows));
        }
    }

    /// <summary>The row above <paramref name="line"/>, if it has one.</summary>
    public IReadOnlyList<LensSegment>? At(ITextSnapshotLine line) =>
        MapFor(line.Snapshot).TryGetValue(line.LineNumber, out var row) ? row.Segments : null;

    /// <summary>The row a command run with the caret on <paramref name="line"/> means.</summary>
    public IReadOnlyList<LensSegment>? ForCaret(ITextSnapshotLine line) =>
        LensRows.ForCaret(MapFor(line.Snapshot).ToDictionary(p => p.Key, p => p.Value.Segments), line.LineNumber, line.GetText());

    private void OnSegmentsChanged(object? sender, string documentUri)
    {
        if (_documentUri is not null && string.Equals(Uri.UnescapeDataString(documentUri), Uri.UnescapeDataString(_documentUri), StringComparison.OrdinalIgnoreCase))
        {
            Refresh();
        }
    }

    // The segments name lines of the text the server last saw, which is the buffer's latest
    // text give or take what was typed in the last half second; they're pinned to it here.
    private void Refresh()
    {
        if (_documentUri is null)
        {
            return;
        }
        var snapshot = _buffer.CurrentSnapshot;
        var rows = LensRows.Build(_providers.SelectMany(p => p.SegmentsFor(_documentUri)), snapshot.LineCount)
            .Select(r => new Row(snapshot.CreateTrackingPoint(snapshot.GetLineFromLineNumber(r.Key).Start, PointTrackingMode.Positive), r.Value))
            .ToArray();
        var previous = Interlocked.Exchange(ref _rows, rows);
        if (previous.Length > 0 || rows.Length > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Layout asks once per formatted line; the line map is kept for the snapshot it was made for.
    private Dictionary<int, Row> MapFor(ITextSnapshot snapshot)
    {
        var rows = Volatile.Read(ref _rows);
        if (Volatile.Read(ref _lineMap) is { } cached && cached.Snapshot == snapshot && ReferenceEquals(cached.Rows, rows))
        {
            return cached.Map;
        }
        var map = new Dictionary<int, Row>();
        foreach (var row in rows)
        {
            // Two rows pushed onto one line by an edit: the first keeps it until the next refresh.
            var line = snapshot.GetLineNumberFromPosition(row.Anchor.GetPosition(snapshot));
            if (!map.ContainsKey(line))
            {
                map[line] = row;
            }
        }
        Volatile.Write(ref _lineMap, new LineMap(snapshot, rows, map));
        return map;
    }

    private sealed record Row(ITrackingPoint Anchor, IReadOnlyList<LensSegment> Segments);

    // Published whole: layout reads it on the UI thread, Ctrl+. may read it on another.
    private sealed record LineMap(ITextSnapshot Snapshot, Row[] Rows, Dictionary<int, Row> Map);
}
