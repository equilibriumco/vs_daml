using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>Where a project's bindings stand with background sync.</summary>
public enum BindingSyncState
{
    /// <summary>A Daml edit concerns them; a refresh is due.</summary>
    Stale,

    /// <summary>Being refreshed.</summary>
    Updating,

    /// <summary>Up to date with the Daml sources.</summary>
    Ready,

    /// <summary>The last refresh failed; the bindings before it stay.</summary>
    Failed,

    /// <summary>The refresh was stopped and nothing follows it (cancelled, another solution); the bindings before it stay.</summary>
    Cancelled,
}

/// <summary>A change in where a project's bindings stand.</summary>
/// <param name="Target">The project.</param>
/// <param name="State">Where its bindings stand now.</param>
/// <param name="Message">Why, for a failure, or what brought them up to date; null otherwise.</param>
public sealed record BindingSyncStatus(BindingRefreshTarget Target, BindingSyncState State, string? Message);

/// <summary>How a refresh went.</summary>
/// <param name="Succeeded">Whether the bindings are up to date now.</param>
/// <param name="Message">What went wrong; null on success.</param>
public sealed record BindingRefreshResult(bool Succeeded, string? Message);

/// <summary>Brings one project's bindings up to date, however the host does it.</summary>
public interface IBindingRefresher
{
    /// <summary>Refreshes <paramref name="target"/>; stops when <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <exception cref="OperationCanceledException">The refresh was cancelled.</exception>
    Task<BindingRefreshResult> RefreshAsync(BindingRefreshTarget target, CancellationToken cancellationToken);
}

/// <summary>
/// The background refreshes of bindings. A project asked for is refreshed once two seconds have
/// passed without another request, one project at a time; asking again for one being refreshed
/// cancels that run, which is out of date, and queues a new one. While Visual Studio builds, nothing
/// runs and a running refresh stops. After a successful build or clean, pending requests are
/// checked again: the global build event does not identify which projects were refreshed.
/// </summary>
public sealed class BindingSyncScheduler : IDisposable
{
    /// <summary>How long requests have to stop before a project is refreshed.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly IBindingRefresher _refresher;
    private readonly TimeProvider _time;
    private readonly IDamlLogger _logger;
    private readonly Debouncer _quiet;
    private readonly List<(BindingRefreshTarget Target, DateTimeOffset Asked)> _pending = [];
    private (BindingRefreshTarget Target, DateTimeOffset Asked, CancellationTokenSource Cancel)? _running;
    private DateTimeOffset _buildStarted;
    private bool _building;
    private bool _pumping;
    private bool _disposed;

    /// <summary>Creates the scheduler.</summary>
    public BindingSyncScheduler(IBindingRefresher refresher, TimeProvider time, IDamlLogger logger)
    {
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _quiet = new Debouncer(Quiet, time);
    }

    /// <summary>Raised when a project's bindings go stale, start or finish refreshing, or a build took them over.</summary>
    public event EventHandler<BindingSyncStatus>? StatusChanged;

    /// <summary>A Daml edit concerns <paramref name="targets"/>.</summary>
    public void Request(IEnumerable<BindingRefreshTarget> targets)
    {
        if (targets is null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        var asked = _time.GetUtcNow();
        var stale = targets.Distinct().ToList();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            foreach (var target in stale)
            {
                _pending.RemoveAll(p => p.Target == target);
                _pending.Add((target, asked));
                if (_running is { } running && running.Target == target)
                {
                    running.Cancel.Cancel();
                }
            }
        }
        foreach (var target in stale)
        {
            Raise(target, BindingSyncState.Stale, null);
        }
        _ = RunWhenQuietAsync();
    }

    /// <summary>Visual Studio started a build or clean: nothing runs until it is over.</summary>
    public void BuildStarted()
    {
        lock (_gate)
        {
            _building = true;
            _buildStarted = _time.GetUtcNow();
            if (_running is { } running)
            {
                if (!_pending.Any(p => p.Target == running.Target))
                {
                    _pending.Insert(0, (running.Target, running.Asked));
                }
                running.Cancel.Cancel();
            }
        }
    }

    /// <summary>Resumes pending refreshes after success; reports failure for requests interrupted by a failed build.</summary>
    public void BuildFinished(bool succeeded = true)
    {
        List<BindingRefreshTarget> covered;
        lock (_gate)
        {
            _building = false;
            covered = succeeded ? [] : _pending.Where(p => p.Asked <= _buildStarted).Select(p => p.Target).ToList();
            if (!succeeded)
            {
                _pending.RemoveAll(p => p.Asked <= _buildStarted);
            }
        }
        foreach (var target in covered)
        {
            Raise(target, BindingSyncState.Failed, "The build failed or was cancelled; the bindings may be out of date.");
        }
        _ = RunAsync();
    }

    /// <summary>Another solution, or a project added or removed: everything queued is dropped and a running refresh stops.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _running?.Cancel.Cancel();
        }
    }

    /// <summary>Stops the running refresh; nothing runs any more.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
            _running?.Cancel.Cancel();
        }
        _quiet.Dispose();
    }

    private async Task RunWhenQuietAsync()
    {
        if (await _quiet.WaitAsync().ConfigureAwait(false))
        {
            await RunAsync().ConfigureAwait(false);
        }
    }

    // One refresh at a time, of requests that have been quiet long enough; fresher ones run when their own wait ends.
    private async Task RunAsync()
    {
        lock (_gate)
        {
            if (_pumping)
            {
                return;
            }
            _pumping = true;
        }
        try
        {
            while (Next() is { } next)
            {
                Raise(next.Target, BindingSyncState.Updating, null);
                BindingRefreshResult? result = null;
                try
                {
                    result = await _refresher.RefreshAsync(next.Target, next.Cancel.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (next.Cancel.IsCancellationRequested)
                {
                    // Asked for again, stopped for a build, or dropped; whatever is still due is queued.
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result = new BindingRefreshResult(false, ex.Message);
                }
                bool cancelled;
                bool followed;
                lock (_gate)
                {
                    cancelled = next.Cancel.IsCancellationRequested;
                    // A newer request or a build interruption leaves another refresh queued.
                    followed = _pending.Any(p => p.Target == next.Target);
                    _running = null;
                }
                next.Cancel.Dispose();
                if (cancelled)
                {
                    if (!followed)
                    {
                        Raise(next.Target, BindingSyncState.Cancelled, null);
                    }
                }
                else if (result is not null)
                {
                    Raise(next.Target, result.Succeeded ? BindingSyncState.Ready : BindingSyncState.Failed, result.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nobody awaits this pump, so an exception here would go unobserved; the next request
            // starts a fresh one.
            lock (_gate)
            {
                _pumping = false;
                _running = null;
            }
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, "The background binding refresh stopped.", ex);
        }
    }

    private (BindingRefreshTarget Target, CancellationTokenSource Cancel)? Next()
    {
        lock (_gate)
        {
            var due = _time.GetUtcNow() - Quiet;
            var index = _pending.FindIndex(p => p.Asked <= due);
            if (_disposed || _building || index < 0)
            {
                // Release ownership while still checking the queue under the same lock.
                // Otherwise a concurrent wake-up can see an active pump just as it exits.
                _pumping = false;
                return null;
            }
            var (target, asked) = _pending[index];
            _pending.RemoveAt(index);
            var cancel = new CancellationTokenSource();
            _running = (target, asked, cancel);
            return (target, cancel);
        }
    }

    // The handlers are the host's (Error List, status bar); their failures are theirs to log and
    // must not take the refreshes with them.
    private void Raise(BindingRefreshTarget target, BindingSyncState state, string? message)
    {
        try
        {
            StatusChanged?.Invoke(this, new BindingSyncStatus(target, state, message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, $"A binding status handler failed for {target.Project}.", ex);
        }
    }
}
