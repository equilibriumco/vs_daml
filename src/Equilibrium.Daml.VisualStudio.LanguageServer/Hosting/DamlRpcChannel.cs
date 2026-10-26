using System;
using StreamJsonRpc;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;

/// <summary>
/// The JSON-RPC connection VS uses for the current server session, for features that send
/// their own messages (custom <c>daml/*</c> requests, keep-alive, script results). It changes
/// with every session; work started on one session must check the id before using a reply.
/// </summary>
public interface IDamlRpcChannel
{
    /// <summary>Raised when a session's connection is attached or let go.</summary>
    event EventHandler? Changed;

    /// <summary>The connection and its session, if one is attached.</summary>
    bool TryGet(out JsonRpc rpc, out int sessionId);
}

/// <summary>Holds at most one session's connection.</summary>
internal sealed class DamlRpcChannel : IDamlRpcChannel
{
    private readonly object _gate = new();
    private JsonRpc? _rpc;
    private int _sessionId;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool TryGet(out JsonRpc rpc, out int sessionId)
    {
        lock (_gate)
        {
            rpc = _rpc!;
            sessionId = _sessionId;
            return _rpc is not null;
        }
    }

    /// <summary>Makes <paramref name="rpc"/> the connection of <paramref name="sessionId"/>.</summary>
    public void Attach(JsonRpc rpc, int sessionId)
    {
        lock (_gate)
        {
            _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
            _sessionId = sessionId;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets go of the connection if it still belongs to <paramref name="sessionId"/>.</summary>
    public void Detach(int sessionId)
    {
        lock (_gate)
        {
            if (_rpc is null || _sessionId != sessionId)
            {
                return;
            }
            _rpc = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
