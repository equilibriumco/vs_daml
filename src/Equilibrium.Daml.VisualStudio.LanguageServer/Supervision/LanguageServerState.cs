using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Supervision;

/// <summary>Where the language server is in its life.</summary>
public enum LanguageServerState
{
    /// <summary>Not running and not asked to.</summary>
    Stopped,

    /// <summary>Creating the process.</summary>
    Starting,

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
    internal LanguageServerSession(int id, Stream input, Stream output, CancellationToken ending)
    {
        Id = id;
        Input = input;
        Output = output;
        Ending = ending;
    }

    /// <summary>Increases with every start; events and replies of older sessions are ignored.</summary>
    public int Id { get; }

    /// <summary>What the client writes to: the server's standard input.</summary>
    public Stream Input { get; }

    /// <summary>What the client reads from: the server's standard output.</summary>
    public Stream Output { get; }

    /// <summary>
    /// Cancelled as soon as the session starts to end (stop, crash or replacement), before the
    /// process goes; work tied to this session should stop when it fires.
    /// </summary>
    public CancellationToken Ending { get; }
}

/// <summary>A line the server wrote to stderr.</summary>
public sealed class LanguageServerStdErrEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public LanguageServerStdErrEventArgs(int sessionId, string line)
    {
        SessionId = sessionId;
        Line = line;
    }

    /// <summary>The session that wrote it.</summary>
    public int SessionId { get; }

    /// <summary>The line, without its line break.</summary>
    public string Line { get; }
}

/// <summary>Timeouts and limits of <see cref="LanguageServerSupervisor"/>.</summary>
public sealed record SupervisorSettings
{
    /// <summary>
    /// How long the server may take to answer <c>initialize</c> before it is treated as crashed.
    /// Generous, because a first start may install an SDK.
    /// </summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a stopped server gets to exit on its own before its tree is killed.</summary>
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>stderr lines logged per second; the rest are counted and summarised.</summary>
    public int StderrLinesPerSecond { get; init; } = 50;

    /// <summary>How many recent stderr lines are kept for diagnostics.</summary>
    public int RetainedStderrLines { get; init; } = 500;
}
