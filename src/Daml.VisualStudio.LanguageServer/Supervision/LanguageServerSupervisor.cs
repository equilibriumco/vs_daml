using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;

namespace Daml.VisualStudio.LanguageServer.Supervision;

/// <summary>
/// Owns the damlc process and decides when it may start. It never swaps a process under a live
/// connection: the LSP client asks for a session when VS activates it (VS does that by itself
/// after a crash), and the supervisor answers with a fresh process after the backoff, or with
/// nothing once the circuit is open. Restarts it wants for other reasons are requested through
/// <see cref="RestartRequested"/> and carried out by the client.
/// </summary>
public sealed class LanguageServerSupervisor : IDisposable
{
    private readonly IProcessRunner _runner;
    private readonly Func<CancellationToken, Task<ProcessSpec>> _buildCommand;
    private readonly IDamlLogger _logger;
    private readonly TimeProvider _time;
    private readonly RestartPolicy _policy;
    private readonly SemaphoreSlim _starts = new(1, 1);
    private readonly object _gate = new();
    private readonly List<string> _pendingReasons = new();
    private LanguageServerState _state = LanguageServerState.Stopped;
    private int _sessionId;
    private IRunningProcess? _process;
    private TimeSpan _backoff;

    /// <summary>Creates a supervisor.</summary>
    /// <param name="runner">Starts the server process.</param>
    /// <param name="buildCommand">Produces the server command for each start, so a start picks up current settings.</param>
    /// <param name="logger">Where crashes and starts are logged.</param>
    /// <param name="time">Clock for the backoff; the system clock if <c>null</c>.</param>
    /// <param name="policy">Backoff and circuit breaker; the default policy if <c>null</c>.</param>
    public LanguageServerSupervisor(
        IProcessRunner runner,
        Func<CancellationToken, Task<ProcessSpec>> buildCommand,
        IDamlLogger logger,
        TimeProvider? time = null,
        RestartPolicy? policy = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _buildCommand = buildCommand ?? throw new ArgumentNullException(nameof(buildCommand));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _policy = policy ?? new RestartPolicy();
    }

    /// <summary>Raised after every state change, on whatever thread caused it.</summary>
    public event EventHandler<LanguageServerStateChangedEventArgs>? StateChanged;

    /// <summary>Raised once per pending restart; further reasons join it until the next start.</summary>
    public event EventHandler<RestartRequestedEventArgs>? RestartRequested;

    /// <summary>Current state.</summary>
    public LanguageServerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Reasons collected for the restart that hasn't happened yet.</summary>
    public IReadOnlyList<string> PendingRestartReasons
    {
        get
        {
            lock (_gate)
            {
                return _pendingReasons.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts a new server session, after the backoff if the last one crashed. Any session still
    /// running is ended first. Returns <c>null</c> while the circuit is open.
    /// </summary>
    public async Task<LanguageServerSession?> StartAsync(CancellationToken cancellationToken)
    {
        await _starts.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IRunningProcess? superseded;
            TimeSpan backoff;
            lock (_gate)
            {
                if (_state == LanguageServerState.Faulted)
                {
                    return null;
                }
                // Forgetting it first means its exit no longer counts as a crash.
                superseded = _process;
                _process = null;
                backoff = _backoff;
            }
            superseded?.Dispose();

            if (backoff > TimeSpan.Zero)
            {
                var before = SetState(LanguageServerState.Restarting);
                try
                {
                    await _time.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    SetState(before);
                    throw;
                }
            }

            SetState(LanguageServerState.Starting);
            IRunningProcess process;
            try
            {
                var command = await _buildCommand(cancellationToken).ConfigureAwait(false);
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information,
                    $"Starting {command.FileName} {string.Join(" ", command.Arguments)} in {command.WorkingDirectory}");
                process = _runner.Start(command);
            }
            catch (OperationCanceledException)
            {
                SetState(LanguageServerState.Stopped);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "The language server couldn't be started.", ex);
                RecordCrash();
                throw;
            }

            int sessionId;
            lock (_gate)
            {
                sessionId = ++_sessionId;
                _process = process;
                _backoff = TimeSpan.Zero;
                _pendingReasons.Clear();
            }
            SetState(LanguageServerState.Running);
            _ = process.Exited.ContinueWith(exited => OnExited(process, sessionId, exited.Result), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            SetState(LanguageServerState.Initializing);
            return new LanguageServerSession(sessionId, process.StandardInput, process.StandardOutput);
        }
        finally
        {
            _starts.Release();
        }
    }

    /// <summary>The client got the reply to <c>initialize</c> for <paramref name="sessionId"/>.</summary>
    public void MarkReady(int sessionId)
    {
        lock (_gate)
        {
            if (sessionId != _sessionId || _state != LanguageServerState.Initializing)
            {
                return;
            }
        }
        SetState(LanguageServerState.Ready);
    }

    /// <summary>
    /// Asks for a restart. The first reason raises <see cref="RestartRequested"/>; more reasons
    /// before the next start join the same restart.
    /// </summary>
    public void RequestRestart(string reason)
    {
        IReadOnlyList<string> reasons;
        lock (_gate)
        {
            if (_pendingReasons.Contains(reason))
            {
                return;
            }
            _pendingReasons.Add(reason);
            if (_pendingReasons.Count > 1)
            {
                return;
            }
            reasons = _pendingReasons.ToArray();
        }
        RestartRequested?.Invoke(this, new RestartRequestedEventArgs(reasons));
    }

    /// <summary>The user's "Restart": forgets past crashes, closes the circuit and requests a restart.</summary>
    public void ResetCircuit()
    {
        bool wasFaulted;
        lock (_gate)
        {
            _policy.Reset();
            _backoff = TimeSpan.Zero;
            wasFaulted = _state == LanguageServerState.Faulted;
        }
        if (wasFaulted)
        {
            SetState(LanguageServerState.Stopped);
        }
        RequestRestart("Restart requested by the user");
    }

    /// <summary>Ends the current process tree, if any.</summary>
    public void Dispose()
    {
        IRunningProcess? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        process?.Dispose();
        _starts.Dispose();
    }

    private void OnExited(IRunningProcess process, int sessionId, int exitCode)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process))
            {
                return; // replaced or stopped: not a crash
            }
            _process = null;
        }
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"The language server (session {sessionId}) exited with code {exitCode}.");
        process.Dispose();
        RecordCrash();
    }

    private void RecordCrash()
    {
        RestartDecision decision;
        lock (_gate)
        {
            decision = _policy.RecordCrash(_time.GetUtcNow());
            _backoff = decision.Delay;
        }
        if (decision.OpenCircuit)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "The language server keeps crashing; it won't be restarted until you restart it.");
            SetState(LanguageServerState.Faulted);
        }
        else
        {
            SetState(LanguageServerState.Crashed);
        }
    }

    // Returns the state it replaced.
    private LanguageServerState SetState(LanguageServerState state)
    {
        LanguageServerState old;
        int sessionId;
        lock (_gate)
        {
            old = _state;
            if (old == state)
            {
                return old;
            }
            _state = state;
            sessionId = _sessionId;
        }
        StateChanged?.Invoke(this, new LanguageServerStateChangedEventArgs(old, state, sessionId));
        return old;
    }
}
