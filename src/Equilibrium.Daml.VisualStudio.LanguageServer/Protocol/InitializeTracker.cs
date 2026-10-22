using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>Why the server refused <c>initialize</c>.</summary>
public sealed class InitializeFailedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public InitializeFailedEventArgs(string message) => Message = message;

    /// <summary>The server's error message.</summary>
    public string Message { get; }
}

/// <summary>
/// Follows the <c>initialize</c> handshake of one session in its traffic: the client side records
/// the request id and later the client's <c>initialized</c>, the server side the matching reply.
/// VS handles the reply before any middle layer sees it, so this is the only reliable signal. The
/// session counts as initialized only once both have happened, in whichever order they are seen:
/// VS treats a server that dies before its <c>initialized</c> as one that failed to start, and
/// doesn't start it again by itself.
/// </summary>
public sealed class InitializeTracker
{
    private static readonly byte[] IdProperty = Encoding.UTF8.GetBytes("\"id\"");

    private readonly object _gate = new();
    private JToken? _expectedId;
    private bool _replied;
    private bool _clientSaidInitialized;
    private bool _done;

    /// <summary>
    /// The server answered <c>initialize</c> with a result and the client's <c>initialized</c>
    /// reached it: the handshake is complete.
    /// </summary>
    public event EventHandler? Initialized;

    /// <summary>The server answered <c>initialize</c> with an error.</summary>
    public event EventHandler<InitializeFailedEventArgs>? Failed;

    /// <summary>Whether an <c>initialize</c> request went out and its reply hasn't been seen.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _expectedId is not null && !_replied;
            }
        }
    }

    internal void Expect(JToken requestId)
    {
        lock (_gate)
        {
            _expectedId = requestId.DeepClone();
            _replied = false;
            _clientSaidInitialized = false;
            _done = false;
        }
    }

    // Called once the client's initialized has been passed on to the server.
    internal void ClientSaidInitialized()
    {
        lock (_gate)
        {
            if (_expectedId is null || _done)
            {
                return;
            }
            _clientSaidInitialized = true;
            if (!_replied)
            {
                return;
            }
            _done = true;
        }
        Initialized?.Invoke(this, EventArgs.Empty);
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

        var failed = message["error"] is JObject;
        lock (_gate)
        {
            if (_replied || !JToken.DeepEquals(message["id"], _expectedId))
            {
                return;
            }
            _replied = true;
            if (!failed && !_clientSaidInitialized)
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
            Initialized?.Invoke(this, EventArgs.Empty);
        }
    }
}
