using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>
/// Hides document closes from damlc, which stops serving a document that is closed and opened
/// again. Shared by the two adapter streams of one session: the client side drops
/// <c>didClose</c> and turns a later <c>didOpen</c> into a full-text <c>didChange</c>; the server
/// side remembers the last diagnostics damlc sent for each document and hands them back on
/// reopen, because Visual Studio drops them on close while damlc thinks they are still shown.
/// </summary>
public sealed class OpenDocuments
{
    private static readonly byte[] DidOpen = Encoding.UTF8.GetBytes("\"textDocument/didOpen\"");
    private static readonly byte[] DidClose = Encoding.UTF8.GetBytes("\"textDocument/didClose\"");
    private static readonly byte[] PublishDiagnostics = Encoding.UTF8.GetBytes("\"textDocument/publishDiagnostics\"");

    private readonly object _gate = new();
    private readonly HashSet<string> _closedByClient = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _lastDiagnostics = new(StringComparer.OrdinalIgnoreCase);

    // Raised with a diagnostics message for the client to see again.
    internal event Action<byte[]>? Redeliver;

    // What to send the server instead of a client message: the same body, another body, or nothing.
    internal byte[]? FromClient(byte[] body)
    {
        var isOpen = ClientMessageSanitizer.Contains(body, DidOpen);
        if (!isOpen && !ClientMessageSanitizer.Contains(body, DidClose))
        {
            return body;
        }
        if (Parse(body) is not { } message || (string?)message["method"] is not { } method)
        {
            return body;
        }

        var document = message["params"]?["textDocument"];
        if ((string?)document?["uri"] is not { } uri)
        {
            return body;
        }
        var key = Key(uri);

        if (method == "textDocument/didClose")
        {
            lock (_gate)
            {
                _closedByClient.Add(key);
            }
            return null;
        }
        if (method != "textDocument/didOpen")
        {
            return body;
        }

        if (document["version"] is not { Type: JTokenType.Integer })
        {
            return body; // a change needs a version to carry; without one the open goes as is
        }
        byte[]? cached;
        lock (_gate)
        {
            if (!_closedByClient.Remove(key))
            {
                return body;
            }
            _lastDiagnostics.TryGetValue(key, out cached);
        }

        var change = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/didChange",
            ["params"] = new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = document!["version"]?.DeepClone() },
                ["contentChanges"] = new JArray { new JObject { ["text"] = document["text"]?.DeepClone() } },
            },
        };
        if (cached is not null)
        {
            Redeliver?.Invoke(Restamped(cached, document["version"]));
        }
        return LspJson.ToBytes(change);
    }

    // Remembers diagnostics damlc sent, so they can be shown again after a reopen. Returns false
    // for a message the client must not see.
    //
    // Only versioned diagnostics are real results: the package's server stamps them with the
    // document version they were computed for. damlc multi-ide's coordinator also sends an empty,
    // unversioned list for every file it knows whenever another file opens, and the package's
    // server never sends the errors again after it, so that clear would wipe errors that still
    // hold. It is dropped when the last real result had errors.
    internal bool FromServer(byte[] body)
    {
        if (!ClientMessageSanitizer.Contains(body, PublishDiagnostics) || Parse(body) is not { } message)
        {
            return true;
        }
        if ((string?)message["method"] != "textDocument/publishDiagnostics" || (string?)message["params"]?["uri"] is not { } uri)
        {
            return true;
        }

        var parameters = message["params"]!;
        var versioned = parameters["version"] is { Type: not JTokenType.Null };
        var empty = parameters["diagnostics"] is not JArray { Count: > 0 };
        var key = Key(uri);
        lock (_gate)
        {
            if (versioned)
            {
                _lastDiagnostics[key] = body;
                return true;
            }
            return !(empty && _lastDiagnostics.TryGetValue(key, out var last) && HasErrors(last));
        }
    }

    private static bool HasErrors(byte[] diagnostics) =>
        Parse(diagnostics)?["params"]?["diagnostics"] is JArray { Count: > 0 };

    // damlc stamps diagnostics with the document version they were computed for, and VS ignores
    // them for an older version than the one it has open. The text is unchanged since then, so
    // they hold for the reopened version too.
    private static byte[] Restamped(byte[] diagnostics, JToken? version)
    {
        if (Parse(diagnostics) is not { } message || message["params"] is not JObject parameters)
        {
            return diagnostics;
        }
        if (version is null || version.Type == JTokenType.Null)
        {
            parameters.Remove("version");
        }
        else
        {
            parameters["version"] = version.DeepClone();
        }
        return LspJson.ToBytes(message);
    }

    // damlc and VS spell the same file differently (escaped drive colon, letter case).
    private static string Key(string uri) => Uri.UnescapeDataString(uri);

    private static JObject? Parse(byte[] body) => LspJson.Parse(body);
}
