using System;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Messages;
using StreamJsonRpc;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;

/// <summary>How often the server is asked whether it is still alive, and how long it may take to answer.</summary>
public sealed record KeepAliveSettings
{
    /// <summary>Whether the server is watched at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Wait after the session is ready, and after each answer, before the next request.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a request may go unanswered before the server counts as hung.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// Notices a server that is still running but no longer answering. While a session is watched it
/// sends <c>daml/keepAlive</c> a minute after the session is ready and a minute after each answer,
/// never more than one at a time; a request left unanswered for two minutes reports the session
/// as unresponsive, once, and watching ends. Any reply counts as alive, errors included. A crash
/// or a server without the method also ends watching, without a report: those are someone
/// else's business.
/// </summary>
internal sealed class KeepAliveWatchdog : IDisposable
{
    private readonly IDamlRpcChannel _channel;
    private readonly Action<int> _unresponsive;
    private readonly IDamlLogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<JsonRpc, CancellationToken, Task> _ping;
    private readonly object _gate = new();
    private int _answered;
    private KeepAliveSettings _settings;
    private Run? _watching;
    private Run? _ended;

    /// <summary>Creates a watchdog; nothing is sent until <see cref="Watch"/>.</summary>
    /// <param name="channel">The current session's connection.</param>
    /// <param name="unresponsive">Called with the session that stopped answering.</param>
    /// <param name="logger">Where pings that fail are logged.</param>
    /// <param name="time">Clock for the interval and timeout; the system clock if <c>null</c>.</param>
    /// <param name="settings">Interval and timeout; the defaults if <c>null</c>.</param>
    /// <param name="ping">Sends one keep-alive request; <c>daml/keepAlive</c> if <c>null</c>.</param>
    public KeepAliveWatchdog(
        IDamlRpcChannel channel,
        Action<int> unresponsive,
        IDamlLogger logger,
        TimeProvider? time = null,
        KeepAliveSettings? settings = null,
        Func<JsonRpc, CancellationToken, Task>? ping = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _unresponsive = unresponsive ?? throw new ArgumentNullException(nameof(unresponsive));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _settings = settings ?? new KeepAliveSettings();
        _ping = ping ?? DamlRequests.KeepAliveAsync;
    }

    /// <summary>The settings the next <see cref="Watch"/> uses; a session already watched keeps its own.</summary>
    public KeepAliveSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
        set
        {
            lock (_gate)
            {
                _settings = value ?? throw new ArgumentNullException(nameof(value));
            }
        }
    }

    /// <summary>Starts watching <paramref name="sessionId"/>, replacing any session watched so far.</summary>
    public void Watch(int sessionId)
    {
        Run? previous;
        Run? run = null;
        lock (_gate)
        {
            previous = _watching;
            if (_settings.Enabled)
            {
                run = new Run(_settings, sessionId);
            }
            _watching = run;
            _ended = previous ?? _ended;
        }
        previous?.End();
        if (run is null)
        {
            return;
        }
        run.Loop = WatchAsync(run, sessionId, run.Cancellation.Token);
        // The loop is the last user of the token source; it goes once the loop is over.
        _ = run.Loop.ContinueWith(_ => run.Cancellation.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Stops watching; a request still in flight is abandoned and can't report anything.</summary>
    /// <param name="sessionId">Stop only this session; null to stop whichever session is watched.</param>
    public void Stop(int? sessionId = null)
    {
        Run? watching;
        lock (_gate)
        {
            if (sessionId is not null && _watching?.SessionId != sessionId) { return; }
            watching = _watching;
            _watching = null;
            _ended = watching ?? _ended;
        }
        watching?.End();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    // For tests: how many requests have been answered so far.
    internal int Answered => Volatile.Read(ref _answered);

    // For tests driving a fake clock: the latest run is blocked on a timer or a reply, or is over,
    // so nothing will happen until the clock moves or the reply arrives.
    // A run that was ended counts only once its loop has really stopped: until then an advance
    // of the clock could still wake it.
    internal bool IsSettled
    {
        get
        {
            Run? run;
            Run? ended;
            lock (_gate)
            {
                run = _watching;
                ended = _ended;
            }
            var endedIsOver = ended?.Loop is not { IsCompleted: false };
            return endedIsOver && (run is null || run.Loop is not { } loop || loop.IsCompleted || run.WaitingOn is { IsCompleted: false });
        }
    }

    private async Task WatchAsync(Run run, int sessionId, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await run.Wait(_time.Delay(run.Settings.Interval, cancellationToken)).ConfigureAwait(false);
                if (!_channel.TryGet(out var rpc, out var current) || current != sessionId)
                {
                    return;
                }
                if (!await PingAsync(run, rpc, sessionId, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, $"The keep-alive check of session {sessionId} failed.", ex);
        }
    }

    // True when the server answered and watching goes on.
    private async Task<bool> PingAsync(Run run, JsonRpc rpc, int sessionId, CancellationToken cancellationToken)
    {
        var ping = _ping(rpc, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = _time.Delay(run.Settings.Timeout, deadline.Token);
        if (await run.Wait(Task.WhenAny(ping, timeout)).ConfigureAwait(false) != ping)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observe(ping);
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning,
                $"The language server (session {sessionId}) didn't answer daml/keepAlive within {run.Settings.Timeout.TotalSeconds:0} s.");
            _unresponsive(sessionId);
            return false;
        }
        deadline.Cancel();
        Observe(timeout);

        try
        {
            await ping.ConfigureAwait(false);
            Interlocked.Increment(ref _answered);
            return true;
        }
        catch (RemoteMethodNotFoundException)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information,
                $"The language server (session {sessionId}) doesn't know daml/keepAlive; it won't be checked for hangs.");
            return false;
        }
        catch (RemoteInvocationException)
        {
            return true;
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException)
        {
            return false;
        }
    }

    private sealed class Run(KeepAliveSettings settings, int sessionId)
    {
        public int SessionId { get; } = sessionId;

        public KeepAliveSettings Settings { get; } = settings;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Loop { get; set; }

        private Task? _waitingOn;

        public Task? WaitingOn => Volatile.Read(ref _waitingOn);

        public T Wait<T>(T task)
            where T : Task
        {
            Volatile.Write(ref _waitingOn, task);
            return task;
        }

        public void End()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The loop was already over, and took the token source with it.
            }
        }
    }

    // A task left behind must not surface as an unobserved exception later.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
