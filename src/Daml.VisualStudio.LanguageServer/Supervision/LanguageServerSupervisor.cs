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
/// after a crash of a server that had initialized), and the supervisor answers with a fresh
/// process after the backoff, or with nothing once the circuit is open. Restarts it wants for
/// other reasons, a crash during startup among them, are requested through
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
    // The session the client is stopping itself; its exit is a stop, not a crash.
    private Session? _leaving;
    private StartAttempt? _start;
    private TimeSpan _backoff;
    private bool _disposed;
    private int _exitsHandled;

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

    /// <summary>
    /// Raised after every state change, on whatever thread caused it. Changes made from different
    /// threads can reach a handler out of order; <see cref="State"/> always has the latest.
    /// </summary>
    public event EventHandler<LanguageServerStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised once per pending restart; further reasons join it until the restart has been
    /// carried out (<see cref="AcknowledgeRestart"/>) or a session starts.
    /// </summary>
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

    // For tests: how many process exits have been looked at, those of replaced sessions included.
    internal int ExitsHandled => Volatile.Read(ref _exitsHandled);

    /// <summary>
    /// The client is about to stop the running server itself: Visual Studio's StopAsync sends it
    /// shutdown and exit, possibly before <see cref="StopAsync"/> is called. Its leaving from now
    /// on is the stop that was asked for, not a crash.
    /// </summary>
    public void ExpectExit()
    {
        lock (_gate)
        {
            _leaving = _current;
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
        StartAttempt? previous;
        StartAttempt attempt;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state == LanguageServerState.Faulted)
            {
                return null;
            }
            _pendingReasons.Clear();
            previous = _start;
            _start = attempt = new StartAttempt(cancellationToken);
        }
        previous?.Cancel();
        using (attempt)
        {
            try
            {
                // Resolution stays outside the transition lock so Stop can invalidate it promptly.
                var command = await _buildCommand(attempt.Token).ConfigureAwait(false);
                await _transitions.WaitAsync(attempt.Token).ConfigureAwait(false);
                try
                {
                    Session? superseded;
                    TimeSpan backoff;
                    lock (_gate)
                    {
                        CheckStartLocked(attempt);
                        if (_state == LanguageServerState.Faulted)
                        {
                            return null;
                        }
                        superseded = _current;
                        _current = null;
                        backoff = _backoff;
                    }
                    superseded?.Dispose();
                    if (backoff > TimeSpan.Zero)
                    {
                        var before = SetStartState(attempt, LanguageServerState.Restarting);
                        try
                        {
                            await _time.Delay(backoff, attempt.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            SetStartState(attempt, before);
                            throw;
                        }
                    }
                    SetStartState(attempt, LanguageServerState.Starting);
                    int id;
                    lock (_gate)
                    {
                        CheckStartLocked(attempt);
                        id = ++_sessionId;
                    }
                    IRunningProcess process;
                    try
                    {
                        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information,
                            $"Starting {command.FileName} {string.Join(" ", command.Arguments)} in {command.WorkingDirectory}");
                        process = _runner.Start(command);
                    }
                    catch (Exception ex)
                    {
                        if (ex is not OperationCanceledException)
                        {
                            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "The language server couldn't be started.", ex);
                        }
                        SetStartState(attempt, LanguageServerState.Stopped);
                        throw;
                    }
                    var session = new Session(id, process);
                    LanguageServerSession connection;
                    LanguageServerState old;
                    try
                    {
                        lock (_gate)
                        {
                            CheckStartLocked(attempt);
                            connection = new LanguageServerSession(id, process.StandardInput, process.StandardOutput, session.EndingToken);
                            _current = session;
                            _backoff = TimeSpan.Zero;
                            old = _state;
                            _state = LanguageServerState.Initializing;
                        }
                    }
                    catch
                    {
                        session.Dispose();
                        throw;
                    }
                    StateChanged?.Invoke(this, new LanguageServerStateChangedEventArgs(old, LanguageServerState.Initializing, id));
                    _ = process.Exited.ContinueWith(exited => OnExited(session, exited.Result), CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_current, session))
                        {
                            return null;
                        }
                    }
                    _ = PumpStderrAsync(session);
                    _ = WatchInitializeAsync(session);
                    return connection;
                }
                finally
                {
                    _transitions.Release();
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_start, attempt))
                    {
                        _start = null;
                    }
                }
            }
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
        StartAttempt? start;
        lock (_gate)
        {
            ThrowIfDisposed();
            start = _start;
            _start = null;
        }
        start?.Cancel();
        // Cancellation shortens the grace period, but must not leave a session running.
        await _transitions.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Session? session;
            lock (_gate)
            {
                session = _current;
                _current = null;
                _leaving = null;
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
            session.CancelEnding();
            try
            {
                if (grace > TimeSpan.Zero && !await ExitsWithinAsync(session.Process, grace, cancellationToken).ConfigureAwait(false))
                {
                    _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning,
                        $"The language server (session {session.Id}) didn't exit within {grace.TotalSeconds:0.#} s of being stopped; killing it.");
                }
            }
            finally
            {
                // Also when the caller stopped waiting: the session is over either way.
                session.Dispose();
                SetState(LanguageServerState.Stopped);
            }
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>The client got the reply to <c>initialize</c> for <paramref name="sessionId"/>.</summary>
    public void MarkReady(int sessionId) =>
        TrySetState(LanguageServerState.Initializing, LanguageServerState.Ready, sessionId);

    /// <summary>
    /// The connection to <paramref name="sessionId"/> had to be closed because of what came over
    /// it (a malformed or oversized message). The server is still alive, but it's as good as
    /// crashed: it is ended and counted by the restart policy, so a server that keeps doing this
    /// doesn't restart in a tight loop.
    /// </summary>
    public void ReportConnectionFailure(int sessionId, Exception error)
    {
        Session? session;
        Crash crash;
        lock (_gate)
        {
            session = _current;
            if (session is null || session.Id != sessionId)
            {
                return;
            }
            _current = null;
            crash = RecordCrashLocked();
        }
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"The connection to the language server (session {sessionId}) was closed: {error?.Message}");
        session.Dispose();
        Announce(crash);
    }

    /// <summary>
    /// The restart raised by <see cref="RestartRequested"/> has been carried out, or couldn't be
    /// (no workspace to start): reasons gathered since are no longer owed to it, and the next
    /// one raises the event again.
    /// </summary>
    public void AcknowledgeRestart()
    {
        lock (_gate)
        {
            _pendingReasons.Clear();
        }
    }

    /// <summary>
    /// Asks for a restart. The first reason raises <see cref="RestartRequested"/>; more reasons
    /// before the next start join the same restart.
    /// </summary>
    public void RequestRestart(string reason)
    {
        string[] reasons;
        lock (_gate)
        {
            if (_pendingReasons.Contains(reason))
            {
                return;
            }
            _pendingReasons.Add(reason);
            reasons = _pendingReasons.ToArray();
        }
        if (reasons.Length > 1)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"{reason}: joins the restart already asked for ({reasons[0]}).");
            return;
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

    /// <summary>Ends the current process tree, if any; nothing starts afterwards.</summary>
    public void Dispose()
    {
        Session? session;
        StartAttempt? start;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            start = _start;
            _start = null;
            session = _current;
            _current = null;
        }
        start?.Cancel();
        session?.Dispose();
        if (State != LanguageServerState.Faulted)
        {
            SetState(LanguageServerState.Stopped);
        }
        // The semaphore stays: a restart still in flight on another thread releases it on its
        // way out, and disposing it under that thread would turn the release into a crash.
    }

    private void CheckStartLocked(StartAttempt attempt)
    {
        if (_disposed || !ReferenceEquals(_start, attempt))
        {
            throw new OperationCanceledException(attempt.Token);
        }
        attempt.Token.ThrowIfCancellationRequested();
    }

    private LanguageServerState SetStartState(StartAttempt attempt, LanguageServerState next)
    {
        LanguageServerState old;
        int id;
        lock (_gate)
        {
            old = _state;
            if (_disposed || !ReferenceEquals(_start, attempt) || old == next)
            {
                return old;
            }
            _state = next;
            id = _sessionId;
        }
        StateChanged?.Invoke(this, new LanguageServerStateChangedEventArgs(old, next, id));
        return old;
    }

    // Callers hold _gate.
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LanguageServerSupervisor));
        }
    }

    private void OnExited(Session session, int exitCode)
    {
        Interlocked.Increment(ref _exitsHandled);
        Crash crash;
        bool stopped;
        lock (_gate)
        {
            if (!ReferenceEquals(_current, session))
            {
                return; // replaced or stopped: not a crash
            }
            _current = null;
            stopped = ReferenceEquals(_leaving, session);
            _leaving = null;
            // Under the same lock: a stop arriving now finds the crash already recorded, so it
            // can't end up Stopped and then be overwritten by this crash.
            crash = stopped ? default : RecordCrashLocked();
        }
        if (stopped)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"The language server (session {session.Id}) left as the client stopped it.");
            session.Dispose();
            SetState(LanguageServerState.Stopped);
            return;
        }
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"The language server (session {session.Id}) exited with code {exitCode}.");
        session.Dispose();
        Announce(crash);
    }

    // VS waits for the reply to initialize forever; a server stuck there is treated as crashed.
    private async Task WatchInitializeAsync(Session session)
    {
        try
        {
            await _time.Delay(_settings.InitializeTimeout, session.EndingToken).ConfigureAwait(false);
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
        try
        {
            session.Process.KillTree(); // the exit is then handled as a crash
        }
        catch (ObjectDisposedException)
        {
            // Ended by someone else in the meantime.
        }
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
        // Any end of the exit task counts, a cancelled one (the process was disposed) included.
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
            // This runs on the process's exit, where nobody would see an exception: a failing
            // handler is logged, and the restart below is still asked for.
            try
            {
                StateChanged?.Invoke(this, new LanguageServerStateChangedEventArgs(crash.Old, crash.New, crash.SessionId));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "Handling the language server's crash failed.", ex);
            }
        }
        // Visual Studio starts a crashed server again by itself only once it has initialized; one
        // that dies before answering initialize is reported as failed to start and left there. So
        // that restart is asked for here, and goes through the backoff and the circuit like any.
        if (crash.Old == LanguageServerState.Initializing && !crash.OpenCircuit)
        {
            RequestRestart("The language server exited during startup");
        }
    }

    private readonly record struct Crash(LanguageServerState Old, LanguageServerState New, int SessionId, bool OpenCircuit);

    // Moves to `next` only while the state is `expected` and the session is still the current
    // one, all under the lock, so a crash arriving in between can't be overwritten.
    private bool TrySetState(LanguageServerState expected, LanguageServerState next, int sessionId)
    {
        LanguageServerState old;
        lock (_gate)
        {
            if (_current?.Id != sessionId || _state != expected)
            {
                return false;
            }
            old = _state;
            _state = next;
        }
        StateChanged?.Invoke(this, new LanguageServerStateChangedEventArgs(old, next, sessionId));
        return true;
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

    // Cancellation callbacks run outside the lock. Disposal is deferred until concurrent
    // Cancel calls finish, so neither invalidation nor cleanup races with CTS.Dispose.
    private sealed class StartAttempt : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancel;
        private bool _disposed;
        private int _cancellers;

        public StartAttempt(CancellationToken caller)
        {
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(caller);
            Token = _cancel.Token;
        }

        public CancellationToken Token { get; }

        public void Cancel()
        {
            lock (_gate)
            {
                if (_disposed) { return; }
                _cancellers++;
            }
            try { _cancel.Cancel(); }
            finally
            {
                lock (_gate)
                {
                    if (--_cancellers == 0 && _disposed) { _cancel.Dispose(); }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) { return; }
                _disposed = true;
                if (_cancellers == 0) { _cancel.Dispose(); }
            }
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly StartAttempt _ending = new(CancellationToken.None);
        private int _ended;

        public Session(int id, IRunningProcess process)
        {
            Id = id;
            Process = process;
        }

        public int Id { get; }
        public IRunningProcess Process { get; }
        public CancellationToken EndingToken => _ending.Token;
        public void CancelEnding() => _ending.Cancel();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) { return; }
            try { _ending.Cancel(); }
            finally
            {
                try { Process.Dispose(); }
                finally { _ending.Dispose(); }
            }
        }
    }
}
