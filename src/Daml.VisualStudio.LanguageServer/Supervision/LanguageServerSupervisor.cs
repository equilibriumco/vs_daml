using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    private readonly SupervisorSettings _settings;
    private readonly SemaphoreSlim _transitions = new(1, 1);
    private readonly object _gate = new();
    private readonly List<string> _pendingReasons = new();
    private readonly Queue<string> _recentStderr = new();
    private LanguageServerState _state = LanguageServerState.Stopped;
    private int _sessionId;
    private Session? _current;
    private TimeSpan _backoff;

    /// <summary>Creates a supervisor.</summary>
    /// <param name="runner">Starts the server process.</param>
    /// <param name="buildCommand">Produces the server command for each start, so a start picks up current settings.</param>
    /// <param name="logger">Where starts, crashes and the server's stderr go.</param>
    /// <param name="time">Clock for backoff and timeouts; the system clock if <c>null</c>.</param>
    /// <param name="policy">Backoff and circuit breaker; the default policy if <c>null</c>.</param>
    /// <param name="settings">Timeouts and stderr limits; the defaults if <c>null</c>.</param>
    public LanguageServerSupervisor(
        IProcessRunner runner,
        Func<CancellationToken, Task<ProcessSpec>> buildCommand,
        IDamlLogger logger,
        TimeProvider? time = null,
        RestartPolicy? policy = null,
        SupervisorSettings? settings = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _buildCommand = buildCommand ?? throw new ArgumentNullException(nameof(buildCommand));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _policy = policy ?? new RestartPolicy();
        _settings = settings ?? new SupervisorSettings();
    }

    /// <summary>Raised after every state change, on whatever thread caused it.</summary>
    public event EventHandler<LanguageServerStateChangedEventArgs>? StateChanged;

    /// <summary>Raised once per pending restart; further reasons join it until the next start.</summary>
    public event EventHandler<RestartRequestedEventArgs>? RestartRequested;

    /// <summary>Raised for every line the current session writes to stderr, on a background thread.</summary>
    public event EventHandler<LanguageServerStdErrEventArgs>? StdErrLine;

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

    /// <summary>The last stderr lines of any session, oldest first, for diagnostics.</summary>
    public IReadOnlyList<string> RecentStderr
    {
        get
        {
            lock (_gate)
            {
                return _recentStderr.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts a new server session, after the backoff if the last one crashed. Any session still
    /// running is ended first. Returns <c>null</c> while the circuit is open.
    /// </summary>
    public async Task<LanguageServerSession?> StartAsync(CancellationToken cancellationToken)
    {
        await _transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session? superseded;
            TimeSpan backoff;
            lock (_gate)
            {
                if (_state == LanguageServerState.Faulted)
                {
                    return null;
                }
                // Forgetting it first means its exit no longer counts as a crash.
                superseded = _current;
                _current = null;
                backoff = _backoff;
            }
            superseded?.End();

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

            Session session;
            lock (_gate)
            {
                session = new Session(++_sessionId, process);
                _current = session;
                _backoff = TimeSpan.Zero;
                _pendingReasons.Clear();
            }
            SetState(LanguageServerState.Running);
            _ = process.Exited.ContinueWith(exited => OnExited(session, exited.Result), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = PumpStderrAsync(session);
            SetState(LanguageServerState.Initializing);
            _ = WatchInitializeAsync(session);
            return new LanguageServerSession(session.Id, process.StandardInput, process.StandardOutput, session.Ending.Token);
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>
    /// Ends the current session. The LSP client sends <c>shutdown</c>/<c>exit</c> itself; this
    /// waits for the process to leave on its own and kills the tree when it doesn't in time.
    /// The session's <see cref="LanguageServerSession.Ending"/> token is cancelled first, so
    /// nothing in flight acts on a server that's going away.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting; the session is still ended.</param>
    /// <param name="gracePeriod">
    /// How long the process may take to leave on its own; <see cref="SupervisorSettings.StopGracePeriod"/>
    /// if <c>null</c>, and zero to kill it at once (a server that never initialized won't leave).
    /// </param>
    public async Task StopAsync(CancellationToken cancellationToken, TimeSpan? gracePeriod = null)
    {
        var grace = gracePeriod ?? _settings.StopGracePeriod;
        await _transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session? session;
            lock (_gate)
            {
                session = _current;
                _current = null;
            }
            if (session is null)
            {
                if (State != LanguageServerState.Faulted)
                {
                    SetState(LanguageServerState.Stopped);
                }
                return;
            }

            SetState(LanguageServerState.Stopping);
            session.Ending.Cancel();
            if (grace > TimeSpan.Zero && !await ExitsWithinAsync(session.Process, grace, cancellationToken).ConfigureAwait(false))
            {
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning,
                    $"The language server (session {session.Id}) didn't exit within {grace.TotalSeconds:0.#} s of being stopped; killing it.");
            }
            session.End();
            SetState(LanguageServerState.Stopped);
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>The client got the reply to <c>initialize</c> for <paramref name="sessionId"/>.</summary>
    public void MarkReady(int sessionId)
    {
        lock (_gate)
        {
            if (_current?.Id != sessionId || _state != LanguageServerState.Initializing)
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
        Session? session;
        lock (_gate)
        {
            session = _current;
            _current = null;
        }
        session?.End();
        _transitions.Dispose();
    }

    private void OnExited(Session session, int exitCode)
    {
        Crash crash;
        lock (_gate)
        {
            if (!ReferenceEquals(_current, session))
            {
                return; // replaced or stopped: not a crash
            }
            _current = null;
            // Under the same lock: a stop arriving now finds the crash already recorded, so it
            // can't end up Stopped and then be overwritten by this crash.
            crash = RecordCrashLocked();
        }
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"The language server (session {session.Id}) exited with code {exitCode}.");
        session.End();
        Announce(crash);
    }

    // VS waits for the reply to initialize forever; a server stuck there is treated as crashed.
    private async Task WatchInitializeAsync(Session session)
    {
        try
        {
            await _time.Delay(_settings.InitializeTimeout, session.Ending.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_current, session) || _state != LanguageServerState.Initializing)
            {
                return;
            }
        }
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error,
            $"The language server (session {session.Id}) didn't answer initialize within {_settings.InitializeTimeout.TotalSeconds:0.#} s; stopping it.");
        session.Process.KillTree(); // the exit is then handled as a crash
    }

    // damlc logs one entry over several lines and only ends it when the next one starts, so a
    // pending entry is also written out once stderr has been quiet for this long.
    private static readonly TimeSpan QuietFlush = TimeSpan.FromMilliseconds(150);

    private async Task PumpStderrAsync(Session session)
    {
        var assembler = new DamlcLogAssembler();
        var output = new StderrLog(this, session.Id);
        try
        {
            using var reader = new StreamReader(session.Process.StandardError, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Task<string?>? pendingRead = null;
            while (true)
            {
                var read = pendingRead ?? reader.ReadLineAsync();
                pendingRead = null;
                if (assembler.HasPending)
                {
                    using var quiet = new CancellationTokenSource();
                    var delay = _time.Delay(QuietFlush, quiet.Token);
                    if (await Task.WhenAny(read, delay).ConfigureAwait(false) != read)
                    {
                        output.Write(assembler.Flush());
                        pendingRead = read;
                        continue;
                    }
                    quiet.Cancel();
                }

                if (await read.ConfigureAwait(false) is not { } line)
                {
                    break;
                }
                lock (_gate)
                {
                    _recentStderr.Enqueue(line);
                    while (_recentStderr.Count > _settings.RetainedStderrLines)
                    {
                        _recentStderr.Dequeue();
                    }
                }
                StdErrLine?.Invoke(this, new LanguageServerStdErrEventArgs(session.Id, line));
                foreach (var record in assembler.Add(line))
                {
                    output.Write(record);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe went away with the process.
        }
        output.Write(assembler.Flush());
        output.ReportDropped();
    }

    // Writes damlc's log entries to the logger, at most so many per second, so a server spewing
    // stderr can't flood the Output window; what's left out is counted and summarised.
    private sealed class StderrLog(LanguageServerSupervisor owner, int sessionId)
    {
        private DateTimeOffset _windowStart = owner._time.GetUtcNow();
        private int _inWindow;
        private int _dropped;

        public void Write(DamlcLogRecord? record)
        {
            if (record is not { } entry)
            {
                return;
            }
            var now = owner._time.GetUtcNow();
            if (now - _windowStart >= TimeSpan.FromSeconds(1))
            {
                ReportDropped();
                _windowStart = now;
                _inWindow = 0;
            }
            if (++_inWindow <= owner._settings.StderrLinesPerSecond)
            {
                owner._logger.Log(DamlLogCategory.LanguageServer, entry.Level, entry.Text);
            }
            else
            {
                _dropped++;
            }
        }

        public void ReportDropped()
        {
            if (_dropped > 0)
            {
                owner._logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning,
                    $"{_dropped} stderr line(s) of session {sessionId} not shown; the language server wrote more than {owner._settings.StderrLinesPerSecond} per second.");
                _dropped = 0;
            }
        }
    }

    private async Task<bool> ExitsWithinAsync(IRunningProcess process, TimeSpan limit, CancellationToken cancellationToken)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = process.Exited.ContinueWith(_ => exited.TrySetResult(true), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = _time.Delay(limit, stopWaiting.Token);
        var first = await Task.WhenAny(exited.Task, timeout).ConfigureAwait(false);
        stopWaiting.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        return first == exited.Task;
    }

    private void RecordCrash()
    {
        Crash crash;
        lock (_gate)
        {
            crash = RecordCrashLocked();
        }
        Announce(crash);
    }

    // Callers hold _gate.
    private Crash RecordCrashLocked()
    {
        var decision = _policy.RecordCrash(_time.GetUtcNow());
        _backoff = decision.Delay;
        var old = _state;
        _state = decision.OpenCircuit ? LanguageServerState.Faulted : LanguageServerState.Crashed;
        return new Crash(old, _state, _sessionId, decision.OpenCircuit);
    }

    private void Announce(Crash crash)
    {
        if (crash.OpenCircuit)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "The language server keeps crashing; it won't be restarted until you restart it.");
        }
        if (crash.Old != crash.New)
        {
            StateChanged?.Invoke(this, new LanguageServerStateChangedEventArgs(crash.Old, crash.New, crash.SessionId));
        }
    }

    private readonly record struct Crash(LanguageServerState Old, LanguageServerState New, int SessionId, bool OpenCircuit);

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

    private sealed class Session
    {
        private int _ended;

        public Session(int id, IRunningProcess process)
        {
            Id = id;
            Process = process;
        }

        public int Id { get; }

        public IRunningProcess Process { get; }

        // Cancelled when the session ends for any reason, before the process is closed.
        public CancellationTokenSource Ending { get; } = new();

        public void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return;
            }
            Ending.Cancel();
            Process.Dispose(); // closes the job: the whole tree goes
            Ending.Dispose();
        }
    }
}
