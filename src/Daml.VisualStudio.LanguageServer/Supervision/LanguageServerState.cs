using System;
using System.Collections.Generic;
using System.IO;

namespace Daml.VisualStudio.LanguageServer.Supervision;

/// <summary>Where the language server is in its life.</summary>
public enum LanguageServerState
{
    /// <summary>Not running and not asked to.</summary>
    Stopped,

    /// <summary>Building the command and creating the process.</summary>
    Starting,

    /// <summary>The process exists; its streams are about to be handed over.</summary>
    Running,

    /// <summary>The client has the streams and is waiting for the reply to <c>initialize</c>.</summary>
    Initializing,

    /// <summary>Initialized and serving.</summary>
    Ready,

    /// <summary>The process ended without being asked to.</summary>
    Crashed,

    /// <summary>Waiting out the backoff before the next start.</summary>
    Restarting,

    /// <summary>Shutting down on request.</summary>
    Stopping,

    /// <summary>Crashed too often; no more automatic starts until the user restarts.</summary>
    Faulted,
}

/// <summary>A state change.</summary>
public sealed class LanguageServerStateChangedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public LanguageServerStateChangedEventArgs(LanguageServerState oldState, LanguageServerState newState, int sessionId)
    {
        OldState = oldState;
        NewState = newState;
        SessionId = sessionId;
    }

    /// <summary>State before the change.</summary>
    public LanguageServerState OldState { get; }

    /// <summary>State after the change.</summary>
    public LanguageServerState NewState { get; }

    /// <summary>The session the change belongs to; 0 before the first start.</summary>
    public int SessionId { get; }
}

/// <summary>Asks the client to stop and start the server again.</summary>
public sealed class RestartRequestedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public RestartRequestedEventArgs(IReadOnlyList<string> reasons) => Reasons = reasons;

    /// <summary>Why, as known when the request was raised; later reasons join the same restart.</summary>
    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>One run of the server process, as handed to the LSP client.</summary>
public sealed class LanguageServerSession
{
    internal LanguageServerSession(int id, Stream input, Stream output)
    {
        Id = id;
        Input = input;
        Output = output;
    }

    /// <summary>Increases with every start; events and replies of older sessions are ignored.</summary>
    public int Id { get; }

    /// <summary>What the client writes to: the server's standard input.</summary>
    public Stream Input { get; }

    /// <summary>What the client reads from: the server's standard output.</summary>
    public Stream Output { get; }
}
