using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Lenses;

/// <summary>Timing of lens requests.</summary>
public sealed class ScriptLensSettings
{
    /// <summary>How long a document must be quiet before its lenses are asked for again.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the server gets to answer before the document is left without lenses.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Keeps the "Script results" lenses of open Daml documents. Visual Studio never asks the server
/// for code lenses, so this asks itself, over the session's connection, a moment after a
/// document was opened or saved or got new diagnostics (each of which can move or add scripts).
/// A newer request for a document cancels the older one. A request that fails or times out
/// leaves the document without lenses until its next change; a reply from a session that has
/// since ended is dropped.
/// </summary>
public sealed class ScriptLensService : IDisposable
{
    private readonly object _gate = new();
    private readonly IDamlRpcChannel _channel;
    private readonly IDamlLogger _logger;
    private readonly TimeProvider _time;
    private readonly ScriptLensSettings _settings;
    private readonly Func<JsonRpc, string, CancellationToken, Task<JToken?>> _request;
    private readonly Dictionary<string, Document> _documents = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Creates the service for the connections <paramref name="channel"/> hands out.</summary>
    public ScriptLensService(IDamlRpcChannel channel, IDamlLogger logger, TimeProvider? time = null, ScriptLensSettings? settings = null)
        : this(channel, logger, time, settings, request: null)
    {
    }

    // Tests answer the requests themselves.
    internal ScriptLensService(
        IDamlRpcChannel channel,
        IDamlLogger logger,
        TimeProvider? time,
        ScriptLensSettings? settings,
        Func<JsonRpc, string, CancellationToken, Task<JToken?>>? request)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _settings = settings ?? new ScriptLensSettings();
        _request = request ?? RequestAsync;
        _channel.Changed += OnChannelChanged;
    }

    /// <summary>Raised, on a thread-pool thread, when a document's lenses change.</summary>
    public event EventHandler<ScriptLensesChangedEventArgs>? LensesChanged;

    /// <summary>The lenses of <paramref name="documentUri"/> as last received; empty if none.</summary>
    public IReadOnlyList<ScriptLens> GetLenses(string documentUri)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(Key(documentUri), out var document) ? document.Lenses : [];
        }
    }

    /// <summary>
    /// Something happened to <paramref name="documentUri"/> that may change its lenses; they are
    /// asked for once it has been quiet for the debounce time. Only Daml sources have lenses:
    /// damlc publishes diagnostics for daml.yaml as well, and asked for its lenses it would parse it
    /// as a Daml module and report a parse error against it.
    /// </summary>
    public void OnDocumentActivity(string documentUri)
    {
        if (string.IsNullOrEmpty(documentUri) || !Key(documentUri).EndsWith(".daml", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        CancellationTokenSource request;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var key = Key(documentUri);
            if (!_documents.TryGetValue(key, out var document))
            {
                document = new Document(documentUri);
                _documents[key] = document;
            }
            document.Pending?.Cancel();
            document.Pending?.Dispose();
            request = new CancellationTokenSource();
            document.Pending = request;
        }
        _ = RefreshAsync(documentUri, request);
    }

    /// <summary>Stops all requests; no more events are raised.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var document in _documents.Values)
            {
                document.Pending?.Cancel();
                document.Pending?.Dispose();
                document.Pending = null;
            }
        }
        _channel.Changed -= OnChannelChanged;
    }

    private async Task RefreshAsync(string documentUri, CancellationTokenSource request)
    {
        var cancellation = request.Token;
        IReadOnlyList<ScriptLens> lenses;
        try
        {
            await _time.Delay(_settings.Debounce, cancellation).ConfigureAwait(false);
            if (!_channel.TryGet(out var rpc, out var session))
            {
                return; // no server yet; the session's own didOpen will ask again
            }
            using var timeout = _time.CreateCancellationTokenSource(_settings.Timeout);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
            JToken? result;
            try
            {
                result = await _request(rpc, documentUri, both.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"The language server didn't list the scripts of {documentUri} within {_settings.Timeout.TotalSeconds:0} s.");
                result = null;
            }
            if (!_channel.TryGet(out _, out var now) || now != session)
            {
                return; // the session that answered is gone; its lenses may not fit the new one
            }
            lenses = ScriptLensParser.Parse(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return; // a newer request or Dispose took over
        }
        catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"Couldn't list the scripts of {documentUri}: {ex.Message}");
            lenses = [];
        }
        Publish(documentUri, request, lenses);
    }

    private void Publish(string documentUri, CancellationTokenSource request, IReadOnlyList<ScriptLens> lenses)
    {
        lock (_gate)
        {
            if (_disposed || !_documents.TryGetValue(Key(documentUri), out var document) || document.Pending != request)
            {
                return;
            }
            document.Pending = null;
            request.Dispose();
            if (document.Lenses.SequenceEqual(lenses))
            {
                return;
            }
            document.Lenses = lenses;
        }
        LensesChanged?.Invoke(this, new ScriptLensesChangedEventArgs(documentUri, lenses));
    }

    // Lenses belong to the session that computed them. When the connection changes they're
    // dropped; the new session's didOpen of each document asks for them again.
    private void OnChannelChanged(object? sender, EventArgs e)
    {
        List<string> cleared;
        lock (_gate)
        {
            cleared = _documents.Values.Where(d => d.Lenses.Count > 0).Select(d => d.Uri).ToList();
            foreach (var document in _documents.Values)
            {
                document.Pending?.Cancel();
                document.Pending?.Dispose();
                document.Pending = null;
            }
            _documents.Clear();
        }
        foreach (var uri in cleared)
        {
            LensesChanged?.Invoke(this, new ScriptLensesChangedEventArgs(uri, []));
        }
    }

    private static Task<JToken?> RequestAsync(JsonRpc rpc, string documentUri, CancellationToken cancellationToken) =>
        rpc.InvokeWithParameterObjectAsync<JToken?>("textDocument/codeLens", new { textDocument = new { uri = documentUri } }, cancellationToken);

    private static string Key(string uri) => Uri.UnescapeDataString(uri);

    private sealed class Document(string uri)
    {
        public string Uri { get; } = uri;

        public IReadOnlyList<ScriptLens> Lenses { get; set; } = [];

        public CancellationTokenSource? Pending { get; set; }
    }
}
