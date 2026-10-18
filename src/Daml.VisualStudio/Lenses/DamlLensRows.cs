using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Bindings;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.ScriptResults;
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
    private readonly Lazy<RoslynCSharpReferenceSource> _csharp;
    private readonly CSharpReferencesWindow _csharpWindow;
    private readonly IDamlLogger _logger;
    private readonly Lazy<IReadOnlyList<ILensSegmentProvider>> _providers;

    [ImportingConstructor]
    public DamlLensSources(Lazy<DamlLanguageClient> client, Lazy<RoslynCSharpReferenceSource> csharp, CSharpReferencesWindow csharpWindow, IDamlLogger logger)
    {
        _client = client;
        _csharp = csharp;
        _csharpWindow = csharpWindow;
        _logger = logger;
        _providers = new Lazy<IReadOnlyList<ILensSegmentProvider>>(() =>
        {
            var csharpReferences = new CSharpReferenceSegments(_csharp.Value);
            csharpReferences.CountFailed += (_, e) => _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning,
                $"Couldn't count the C# references of {e.Declaration.Name} in {e.DamlFile}.", e.Exception);
            return [new ScriptLensSegments(_client.Value.Host.ScriptLenses), csharpReferences];
        });
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
            case CSharpReferenceLens lens:
                var name = $"{lens.Module}:{lens.Declaration.Name}";
                try
                {
                    var references = await _csharp.Value.FindAsync(lens.DamlFile, lens.Module, lens.Declaration, cancellationToken).ConfigureAwait(false) ?? [];
                    _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"{references.Count} C# reference(s) of {name}.");
                    await _csharpWindow.ShowAsync(name, references, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, $"Couldn't show the C# references of {name}.", ex);
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
internal sealed class DamlLensRows : IDisposable
{
    // Scrolling lays out lines many times a second; what is on screen is told once it stops.
    private static readonly TimeSpan ShownQuiet = TimeSpan.FromMilliseconds(150);

    private readonly ITextBuffer _buffer;
    private readonly string? _documentUri;
    private readonly IReadOnlyList<ILensSegmentProvider> _providers;
    private readonly Debouncer _shown = new(ShownQuiet, TimeProvider.System);
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
            Dispose();
            _buffer.Properties.RemoveProperty(typeof(DamlLensRows));
        }
    }

    /// <summary>Drops what is waiting to be told to the providers; the last view to close does this.</summary>
    public void Dispose() => _shown.Dispose();

    /// <summary>
    /// A view shows lines <paramref name="firstLine"/> to <paramref name="lastLine"/> of
    /// <paramref name="snapshot"/>; the providers that work only for what is on screen hear it once
    /// scrolling stops, the last view to scroll winning.
    /// </summary>
    public void Shown(int firstLine, int lastLine, ITextSnapshot snapshot)
    {
        if (_documentUri is not null && _providers.OfType<IVisibleLensProvider>().Any())
        {
            _ = ShownWhenQuietAsync(_documentUri, firstLine, lastLine, snapshot);
        }
    }

    private async Task ShownWhenQuietAsync(string documentUri, int firstLine, int lastLine, ITextSnapshot snapshot)
    {
        if (!await _shown.WaitAsync().ConfigureAwait(false))
        {
            return;
        }
        foreach (var provider in _providers.OfType<IVisibleLensProvider>())
        {
            provider.Shown(documentUri, firstLine, lastLine, snapshot.Version.VersionNumber, snapshot.GetText);
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
