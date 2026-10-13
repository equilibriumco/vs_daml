using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>Why the server refused <c>initialize</c>.</summary>
public sealed class InitializeFailedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public InitializeFailedEventArgs(string message) => Message = message;

    /// <summary>The server's error message.</summary>
    public string Message { get; }
}

/// <summary>
/// Spots the server's reply to <c>initialize</c> in the traffic of one session: the client side
/// records the request id, the server side reports the matching reply. VS handles that reply
/// before any middle layer sees it, so this is the only reliable "server is ready" signal.
/// </summary>
public sealed class InitializeTracker
{
    private static readonly byte[] IdProperty = Encoding.UTF8.GetBytes("\"id\"");

    private readonly object _gate = new();
    private JToken? _expectedId;
    private bool _done;

    /// <summary>The server answered <c>initialize</c> with a result.</summary>
    public event EventHandler? Responded;

    /// <summary>The server answered <c>initialize</c> with an error.</summary>
    public event EventHandler<InitializeFailedEventArgs>? Failed;

    /// <summary>Whether an <c>initialize</c> request went out and its reply hasn't been seen.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _expectedId is not null && !_done;
            }
        }
    }

    internal void Expect(JToken requestId)
    {
        lock (_gate)
        {
            _expectedId = requestId.DeepClone();
            _done = false;
        }
    }

    internal void Inspect(byte[] body)
    {
        if (!IsPending || !ClientMessageSanitizer.Contains(body, IdProperty))
        {
            return;
        }

        if (LspJson.Parse(body) is not { } message)
        {
            return;
        }
        if (message["method"] is not null || (message["result"] is null && message["error"] is null))
        {
            return; // a request or notification from the server, not a reply
        }

        lock (_gate)
        {
            if (_done || !JToken.DeepEquals(message["id"], _expectedId))
            {
                return;
            }
            _done = true;
        }

        if (message["error"] is JObject error)
        {
            Failed?.Invoke(this, new InitializeFailedEventArgs((string?)error["message"] ?? error.ToString(Newtonsoft.Json.Formatting.None)));
        }
        else
        {
            Responded?.Invoke(this, EventArgs.Empty);
        }
    }
}
