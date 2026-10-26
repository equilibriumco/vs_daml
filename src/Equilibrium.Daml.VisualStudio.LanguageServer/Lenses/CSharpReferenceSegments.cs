using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Lenses;

/// <summary>What a Daml file's package has of generated C#.</summary>
public enum CSharpLinkState
{
    /// <summary>No C# project generates from it.</summary>
    None,

    /// <summary>C# was generated from other sources than the package has now; a build brings it up to date.</summary>
    Stale,

    /// <summary>C# generated from the package as it is.</summary>
    Current,
}

/// <summary>Turns the generations of a package's C# into what its references can be counted from.</summary>
public static class CSharpLinks
{
    /// <summary>
    /// Current when any generation was made from the sources as they are now, with each such
    /// package once; stale when the only ones were made from other sources; otherwise none.
    /// </summary>
    /// <param name="generations">Each generation's package and how its recorded sources compare with the Daml now.</param>
    public static (CSharpLinkState State, IReadOnlyList<string> Packages) From(IEnumerable<(string Package, SourceStatus Status)> generations)
    {
        if (generations is null)
        {
            throw new ArgumentNullException(nameof(generations));
        }
        var all = generations.ToList();
        var current = all.Where(g => g.Status == SourceStatus.Current).Select(g => g.Package).Distinct(StringComparer.Ordinal).ToList();
        return current.Count > 0 ? (CSharpLinkState.Current, current)
            : all.Any(g => g.Status == SourceStatus.Stale) ? (CSharpLinkState.Stale, [])
            : (CSharpLinkState.None, []);
    }
}

/// <summary>What a "C#: N references" segment stands for, for a click to act on.</summary>
/// <param name="DamlFile">The Daml file.</param>
/// <param name="Module">Its module.</param>
/// <param name="Declaration">The template, interface or choice.</param>
public sealed record CSharpReferenceLens(string DamlFile, string Module, DamlLensDeclaration Declaration);

/// <summary>A count of C# references that went wrong, for the log.</summary>
public sealed class CSharpReferenceCountFailedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public CSharpReferenceCountFailedEventArgs(string damlFile, DamlLensDeclaration declaration, Exception exception)
    {
        DamlFile = damlFile;
        Declaration = declaration;
        Exception = exception;
    }

    /// <summary>The Daml file.</summary>
    public string DamlFile { get; }

    /// <summary>The declaration whose references couldn't be counted.</summary>
    public DamlLensDeclaration Declaration { get; }

    /// <summary>What went wrong.</summary>
    public Exception Exception { get; }
}

/// <summary>Where C# references come from: the binding index and the C# solution.</summary>
public interface ICSharpReferenceSource
{
    /// <summary>Raised when the C# solution or the index changed, so every count is out of date.</summary>
    event EventHandler? Changed;

    /// <summary>What the package of <paramref name="damlFile"/> has of generated C#.</summary>
    CSharpLinkState LinkOf(string damlFile);

    /// <summary>The C# uses of <paramref name="declaration"/>, each place once; null when no C# stands for it.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<CSharpReference>?> FindAsync(string damlFile, string damlModule, DamlLensDeclaration declaration, CancellationToken cancellationToken);
}

/// <summary>
/// "C#: N references" above the templates, interfaces and choices of Daml files whose package
/// generates C#. Only declarations on screen are counted, two at a time; scrolling away cancels
/// what is no longer shown; a count stands until the C# solution or the index changes, and shows
/// until its new count arrives. A change never stops a count under way (while a solution loads,
/// changes come faster than counts end): its result shows, and a fresh count follows it. Bindings
/// from older sources say so instead of a count, a declaration no C# stands for (new since the
/// build) has no segment, and a count that fails says so and is tried again after the next change.
/// </summary>
public sealed class CSharpReferenceSegments : IVisibleLensProvider, IDisposable
{
    /// <summary>Position in a row: after the script results.</summary>
    public const int Order = 20;

    /// <summary>How many counts run at once.</summary>
    public const int Concurrency = 2;

    private const string Pending = "C#: …";
    private const string OutOfDate = "C#: out of date (build to update)";
    private const string Failed = "C#: couldn't count (see Output)";

    private readonly ICSharpReferenceSource _source;
    private readonly SemaphoreSlim _running = new(Concurrency, Concurrency);
    private readonly Dictionary<string, Document> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _generation;
    private bool _disposed;

    /// <summary>Creates the provider over <paramref name="source"/>.</summary>
    public CSharpReferenceSegments(ICSharpReferenceSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _source.Changed += OnSourceChanged;
    }

    /// <inheritdoc />
    public event EventHandler<string>? SegmentsChanged;

    /// <summary>Raised, on any thread, when a count failed; the row says so, the log says why.</summary>
    public event EventHandler<CSharpReferenceCountFailedEventArgs>? CountFailed;

    /// <inheritdoc />
    public IReadOnlyList<LensSegment> SegmentsFor(string documentUri)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(documentUri, out var document) || document.Module is null || document.Link == CSharpLinkState.None)
            {
                return [];
            }
            var segments = new List<LensSegment>();
            foreach (var declaration in document.Declarations)
            {
                string text;
                if (document.Link == CSharpLinkState.Stale)
                {
                    text = OutOfDate;
                }
                else if (!document.Counts.TryGetValue(declaration.Name, out var count))
                {
                    text = Pending;
                }
                else if (count.Failed)
                {
                    text = Failed;
                }
                else if (count.Value is { } n)
                {
                    text = CSharpReferences.Text(n);
                }
                else
                {
                    continue; // no C# stands for it
                }
                var kind = declaration.Kind.ToString().ToLowerInvariant();
                segments.Add(new LensSegment(declaration.Line, Order, text, $"Show the C# references of {kind} {declaration.Name}",
                    new CSharpReferenceLens(document.File, document.Module, declaration)));
            }
            return segments;
        }
    }

    /// <inheritdoc />
    public void Shown(string documentUri, int firstLine, int lastLine, int textVersion, Func<string> text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        if (LspUri.ToLocalPath(documentUri) is not { } file)
        {
            return;
        }
        bool changed;
        var stop = new List<CancellationTokenSource>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (!_documents.TryGetValue(documentUri, out var document))
            {
                _documents[documentUri] = document = new Document(file);
            }
            changed = false;
            if (document.TextVersion != textVersion)
            {
                var (module, declarations) = DamlLensDeclarations.Of(text());
                changed = module != document.Module || !declarations.SequenceEqual(document.Declarations);
                (document.Module, document.Declarations, document.TextVersion) = (module, declarations, textVersion);
            }
            var link = _source.LinkOf(file);
            changed |= link != document.Link;
            document.Link = link;
            (document.FirstLine, document.LastLine) = (firstLine, lastLine);
            Schedule(documentUri, document, stop);
        }
        Cancel(stop);
        if (changed)
        {
            SegmentsChanged?.Invoke(this, documentUri);
        }
    }

    /// <inheritdoc />
    public void Hidden(string documentUri)
    {
        List<CancellationTokenSource> stop;
        lock (_gate)
        {
            if (!_documents.TryGetValue(documentUri, out var document))
            {
                return;
            }
            _documents.Remove(documentUri);
            stop = [.. document.Running.Values];
            document.Running.Clear();
        }
        Cancel(stop);
    }

    /// <summary>Stops every count; the source isn't listened to any more.</summary>
    public void Dispose()
    {
        List<CancellationTokenSource> stop;
        lock (_gate)
        {
            _disposed = true;
            stop = [.. _documents.Values.SelectMany(d => d.Running.Values)];
        }
        Cancel(stop);
        _source.Changed -= OnSourceChanged;
    }

    // Outside the lock: a cancelled count may finish on this very thread and take the lock itself.
    private static void Cancel(IEnumerable<CancellationTokenSource> sources)
    {
        foreach (var source in sources)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That count finished in the meantime.
            }
        }
    }

    // Under the lock: counts what is on screen and isn't counted for this generation yet; what scrolled away goes to stop.
    private void Schedule(string documentUri, Document document, List<CancellationTokenSource> stop)
    {
        var shown = document.Link == CSharpLinkState.Current && document.Module is not null
            ? document.Declarations.Where(d => d.Line >= document.FirstLine && d.Line <= document.LastLine).ToList()
            : [];
        foreach (var name in document.Running.Keys.Where(n => !shown.Any(d => d.Name == n)).ToList())
        {
            stop.Add(document.Running[name]);
            document.Running.Remove(name);
        }
        foreach (var declaration in shown)
        {
            var counted = document.Counts.TryGetValue(declaration.Name, out var count) && count.Generation == _generation;
            if (!counted && !document.Running.ContainsKey(declaration.Name))
            {
                var cancel = new CancellationTokenSource();
                document.Running[declaration.Name] = cancel;
                _ = CountAsync(documentUri, document, declaration, _generation, cancel);
            }
        }
    }

    private async Task CountAsync(string documentUri, Document document, DamlLensDeclaration declaration, int generation, CancellationTokenSource cancel)
    {
        IReadOnlyList<CSharpReference>? references = null;
        Exception? failure = null;
        var counted = false;
        try
        {
            await _running.WaitAsync(cancel.Token).ConfigureAwait(false);
            try
            {
                references = await _source.FindAsync(document.File, document.Module!, declaration, cancel.Token).ConfigureAwait(false);
                counted = true;
            }
            finally
            {
                _running.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or closed; whatever is still due is scheduled anew.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
        }
        var stop = new List<CancellationTokenSource>();
        lock (_gate)
        {
            if (document.Running.TryGetValue(declaration.Name, out var current) && ReferenceEquals(current, cancel))
            {
                document.Running.Remove(declaration.Name);
            }
            if ((counted || failure is not null) && !_disposed)
            {
                document.Counts[declaration.Name] = new Count(generation, references?.Count, failure is not null);
                // The C# changed while it counted: this result shows, and a fresh count follows it.
                if (generation != _generation)
                {
                    Schedule(documentUri, document, stop);
                }
            }
        }
        cancel.Dispose();
        Cancel(stop);
        if (failure is not null)
        {
            CountFailed?.Invoke(this, new CSharpReferenceCountFailedEventArgs(document.File, declaration, failure));
        }
        if (counted || failure is not null)
        {
            SegmentsChanged?.Invoke(this, documentUri);
        }
    }

    // The C# or the index changed, or a Daml file was saved: every count is redone as it comes on
    // screen, the old one showing until then; counts under way finish first and are redone after.
    private void OnSourceChanged(object? sender, EventArgs e)
    {
        List<string> changed;
        var stop = new List<CancellationTokenSource>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _generation++;
            changed = [];
            foreach (var pair in _documents)
            {
                var link = _source.LinkOf(pair.Value.File);
                if (link != pair.Value.Link)
                {
                    pair.Value.Link = link;
                    changed.Add(pair.Key);
                }
                Schedule(pair.Key, pair.Value, stop);
            }
        }
        Cancel(stop);
        foreach (var uri in changed)
        {
            SegmentsChanged?.Invoke(this, uri);
        }
    }

    private sealed class Document(string file)
    {
        public string File { get; } = file;

        public int TextVersion { get; set; } = -1;

        public string? Module { get; set; }

        public IReadOnlyList<DamlLensDeclaration> Declarations { get; set; } = [];

        public CSharpLinkState Link { get; set; } = CSharpLinkState.None;

        public int FirstLine { get; set; }

        public int LastLine { get; set; } = -1;

        public Dictionary<string, Count> Counts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, CancellationTokenSource> Running { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Count(int Generation, int? Value, bool Failed);
}
