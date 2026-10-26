using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>
/// Keeps file documents open in damlc, which stops serving files after close/reopen. A reopen
/// becomes a full-text change. Diagnostics are replayed only when their text fingerprint matches.
/// Virtual resources retain their normal open/close lifecycle.
/// </summary>
public sealed class OpenDocuments
{
    private static readonly byte[] DidOpen = Encoding.UTF8.GetBytes("\"textDocument/didOpen\"");
    private static readonly byte[] DidClose = Encoding.UTF8.GetBytes("\"textDocument/didClose\"");
    private static readonly byte[] DidChange = Encoding.UTF8.GetBytes("\"textDocument/didChange\"");
    private static readonly byte[] DidSave = Encoding.UTF8.GetBytes("\"textDocument/didSave\"");
    private static readonly byte[] PublishDiagnostics = Encoding.UTF8.GetBytes("\"textDocument/publishDiagnostics\"");
    private readonly object _gate = new();
    private readonly HashSet<string> _closedByClient = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Document> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedDiagnostics> _lastDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _retentionOrder = new();
    private readonly int _maxDocuments;
    private readonly int _maxBytes;
    private int _retainedBytes;

    /// <summary>Creates a session's document tracker with a bounded diagnostics cache.</summary>
    /// <param name="maxDocuments">Maximum number of retained diagnostics messages.</param>
    /// <param name="maxBytes">Maximum retained diagnostic payload bytes.</param>
    public OpenDocuments(int maxDocuments = 256, int maxBytes = 16 * 1024 * 1024)
    {
        if (maxDocuments <= 0 || maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDocuments), "Cache limits must be positive.");
        }
        _maxDocuments = maxDocuments;
        _maxBytes = maxBytes;
    }

    internal event Action<byte[]>? Redeliver;

    /// <summary>
    /// A file document was opened or saved by Visual Studio, or got diagnostics from the server:
    /// the moments its script lenses can have changed. Raised with the document's URI.
    /// </summary>
    internal event Action<string>? Activity;

    internal byte[]? FromClient(byte[] body)
    {
        if (!ClientMessageSanitizer.Contains(body, DidOpen) && !ClientMessageSanitizer.Contains(body, DidClose)
            && !ClientMessageSanitizer.Contains(body, DidChange) && !ClientMessageSanitizer.Contains(body, DidSave))
        {
            return body;
        }
        var message = LspJson.Parse(body);
        var document = message?["params"]?["textDocument"];
        if ((string?)document?["uri"] is not { } uri || !IsFile(uri))
        {
            return body;
        }
        var key = Key(uri);
        var method = (string?)message?["method"];
        if (method is "textDocument/didOpen" or "textDocument/didSave")
        {
            Activity?.Invoke(uri);
        }
        if (method == "textDocument/didClose")
        {
            lock (_gate)
            {
                _closedByClient.Add(key);
            }
            return null;
        }
        if (method is not ("textDocument/didOpen" or "textDocument/didChange") || document?["version"]?.Type != JTokenType.Integer)
        {
            return body;
        }
        var version = (long)document["version"]!;
        string? text = null;
        if (method == "textDocument/didOpen")
        {
            text = (string?)document["text"];
        }
        else if (message?["params"]?["contentChanges"] is JArray { Count: 1 } changes && changes[0]?["range"] is null)
        {
            text = (string?)changes[0]?["text"];
        }
        // An incremental edit invalidates replay until the next full text arrives. This avoids
        // retaining an extra document copy or treating diagnostics for old text as current.
        var fingerprint = text is null ? null : Fingerprint(text);
        byte[]? replay = null;
        bool reopen;
        lock (_gate)
        {
            _documents[key] = new Document(version, fingerprint);
            reopen = method == "textDocument/didOpen" && _closedByClient.Remove(key);
            if (_lastDiagnostics.TryGetValue(key, out var cached))
            {
                if (fingerprint is not null && fingerprint == cached.Fingerprint)
                {
                    if (reopen)
                    {
                        replay = Restamped(cached.Body, version);
                    }
                }
                else
                {
                    RemoveCached(key);
                }
            }
        }
        if (!reopen)
        {
            return body;
        }
        var change = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/didChange",
            ["params"] = new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = version },
                ["contentChanges"] = new JArray { new JObject { ["text"] = document["text"]?.DeepClone() } },
            },
        };
        if (replay is not null)
        {
            Redeliver?.Invoke(replay);
        }
        return LspJson.ToBytes(change);
    }

    internal bool FromServer(byte[] body)
    {
        if (!ClientMessageSanitizer.Contains(body, PublishDiagnostics) || LspJson.Parse(body) is not { } message
            || LspJson.Text(message["method"]) != "textDocument/publishDiagnostics"
            || LspJson.Text(message["params"]?["uri"]) is not { } uri || !IsFile(uri))
        {
            return true;
        }
        Activity?.Invoke(uri);
        var parameters = message["params"]!;
        var version = parameters["version"];
        var hasErrors = parameters["diagnostics"] is JArray { Count: > 0 };
        var key = Key(uri);
        lock (_gate)
        {
            if (version is { Type: JTokenType.Integer })
            {
                if (!_documents.TryGetValue(key, out var document))
                {
                    return true;
                }
                if ((long)version != document.Version)
                {
                    return false;
                }
                RemoveCached(key);
                if (body.Length <= _maxBytes)
                {
                    while (_lastDiagnostics.Count >= _maxDocuments || _retainedBytes > _maxBytes - body.Length)
                    {
                        RemoveCached(_retentionOrder.Dequeue());
                    }
                    _lastDiagnostics[key] = new CachedDiagnostics(body, document.Fingerprint, hasErrors);
                    _retainedBytes += body.Length;
                    _retentionOrder.Enqueue(key);
                }
                return true;
            }
            // multi-ide sends an unversioned blanket clear when another file opens. Suppress
            // it only while we have an error result associated with the current known text.
            return hasErrors || !_lastDiagnostics.TryGetValue(key, out var last) || !last.HasErrors;
        }
    }

    private void RemoveCached(string key)
    {
        if (_lastDiagnostics.TryGetValue(key, out var old))
        {
            _retainedBytes -= old.Body.Length;
            _lastDiagnostics.Remove(key);
            // The cache is small; removal keeps its FIFO metadata bounded as well as payloads.
            var count = _retentionOrder.Count;
            while (count-- > 0)
            {
                var retained = _retentionOrder.Dequeue();
                if (!string.Equals(retained, key, StringComparison.OrdinalIgnoreCase))
                {
                    _retentionOrder.Enqueue(retained);
                }
            }
        }
    }

    private static byte[] Restamped(byte[] diagnostics, long version)
    {
        var message = LspJson.Parse(diagnostics)!;
        message["params"]!["version"] = version;
        return LspJson.ToBytes(message);
    }

    private static bool IsFile(string uri) => LspUri.ToLocalPath(uri) is not null;

    private static string Key(string uri) => Uri.UnescapeDataString(uri);

    private static string Fingerprint(string text)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private sealed record Document(long Version, string? Fingerprint);

    private sealed record CachedDiagnostics(byte[] Body, string? Fingerprint, bool HasErrors);
}
