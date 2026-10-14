using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.LanguageServer.Protocol;

internal enum SanitizeAction
{
    Forward,
    Drop,
}

internal readonly struct SanitizedMessage
{
    public SanitizedMessage(SanitizeAction action, byte[] body, JToken? initializeRequestId, bool isInitialized = false)
    {
        Action = action;
        Body = body;
        InitializeRequestId = initializeRequestId;
        IsInitialized = isInitialized;
    }

    public SanitizeAction Action { get; }

    // The original array when nothing had to change.
    public byte[] Body { get; }

    // Set when the message is the initialize request, so its reply can be recognised.
    public JToken? InitializeRequestId { get; }

    // The client's initialized notification, which completes the handshake.
    public bool IsInitialized { get; }
}

// damlc decodes strictly and exits on the first enum value it doesn't know, while VS sends
// private ones: completion item kinds 0 and 118115+, diagnostic tags 2147483645/6 (in initialize
// and in diagnostics it echoes back, e.g. in code action requests). VS also forwards its own
// NotificationReceived notification, which the multi-package server chokes on. initialize
// bypasses ILanguageClientMiddleLayer2, so this has to happen on the byte stream.
internal static class ClientMessageSanitizer
{
    private const int FirstCompletionItemKind = 1;
    private const int LastCompletionItemKind = 25;
    private const int FirstDiagnosticTag = 1;
    private const int LastDiagnosticTag = 2;
    private const int FirstSymbolKind = 1;
    private const int LastSymbolKind = 26;
    private const int FirstCompletionItemTag = 1;
    private const int LastCompletionItemTag = 1;
    private const int FirstInsertTextMode = 1;
    private const int LastInsertTextMode = 2;

    private static readonly byte[] NotificationReceived = Encoding.UTF8.GetBytes("\"NotificationReceived\"");
    private static readonly byte[] Initialize = Encoding.UTF8.GetBytes("\"initialize\"");
    private static readonly byte[] Initialized = Encoding.UTF8.GetBytes("\"initialized\"");
    private static readonly byte[] Tags = Encoding.UTF8.GetBytes("\"tags\"");
    private static readonly byte[] Diagnostics = Encoding.UTF8.GetBytes("\"diagnostics\"");

    public static SanitizedMessage Sanitize(byte[] body)
    {
        // Most messages contain none of these, and those go through without being parsed. A
        // document that merely mentions "tags" in its text isn't parsed either: tags to clamp
        // sit inside echoed diagnostics.
        var mayBeNotification = Contains(body, NotificationReceived);
        var mayBeInitialize = Contains(body, Initialize);
        var mayBeInitialized = Contains(body, Initialized);
        var mayHaveTags = Contains(body, Tags) && Contains(body, Diagnostics);
        if (!mayBeNotification && !mayBeInitialize && !mayBeInitialized && !mayHaveTags)
        {
            return new SanitizedMessage(SanitizeAction.Forward, body, null);
        }

        if (LspJson.Parse(body) is not { } message)
        {
            return new SanitizedMessage(SanitizeAction.Forward, body, null); // not ours to judge
        }

        var method = message["method"] is JValue { Type: JTokenType.String } name ? (string?)name : null;
        if (method == "NotificationReceived")
        {
            return new SanitizedMessage(SanitizeAction.Drop, body, null);
        }
        if (method == "initialized")
        {
            return new SanitizedMessage(SanitizeAction.Forward, body, null, isInitialized: true);
        }

        var changed = false;
        JToken? initializeId = null;
        if (method == "initialize")
        {
            initializeId = message["id"];
            changed |= Clamp(message.SelectToken("params.capabilities.textDocument.completion.completionItemKind.valueSet"), FirstCompletionItemKind, LastCompletionItemKind);
            changed |= Clamp(message.SelectToken("params.capabilities.textDocument.publishDiagnostics.tagSupport.valueSet"), FirstDiagnosticTag, LastDiagnosticTag);
            changed |= Clamp(message.SelectToken("params.capabilities.textDocument.documentSymbol.symbolKind.valueSet"), FirstSymbolKind, LastSymbolKind);
            changed |= Clamp(message.SelectToken("params.capabilities.workspace.symbol.symbolKind.valueSet"), FirstSymbolKind, LastSymbolKind);
            changed |= Clamp(message.SelectToken("params.capabilities.textDocument.completion.completionItem.tagSupport.valueSet"), FirstCompletionItemTag, LastCompletionItemTag);
            changed |= Clamp(message.SelectToken("params.capabilities.textDocument.completion.completionItem.insertTextModeSupport.valueSet"), FirstInsertTextMode, LastInsertTextMode);
        }
        if (mayHaveTags)
        {
            foreach (var diagnostic in message.SelectTokens("$..diagnostics[*]").OfType<JObject>().ToList())
            {
                changed |= Clamp(diagnostic["tags"], FirstDiagnosticTag, LastDiagnosticTag);
            }
        }

        var result = changed ? LspJson.ToBytes(message) : body;
        return new SanitizedMessage(SanitizeAction.Forward, result, initializeId);
    }

    // Removes every element of an integer array outside first..last; true if anything went.
    private static bool Clamp(JToken? values, int first, int last)
    {
        if (values is not JArray array)
        {
            return false;
        }
        var outside = array.Where(v => v.Type != JTokenType.Integer || (long)v < first || (long)v > last).ToList();
        foreach (var value in outside)
        {
            value.Remove();
        }
        return outside.Count > 0;
    }

    internal static bool Contains(byte[] haystack, byte[] needle)
    {
        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            if (haystack[i] != needle[0])
            {
                continue;
            }
            var j = 1;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }
            if (j == needle.Length)
            {
                return true;
            }
        }
        return false;
    }
}
