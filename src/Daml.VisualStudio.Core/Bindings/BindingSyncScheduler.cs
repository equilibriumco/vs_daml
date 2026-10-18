using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.ScriptResults;

namespace Daml.VisualStudio.Core.Bindings;

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
/// runs and a running refresh stops; once the build is over, what was asked for before it began is
/// left to it, since it regenerated those bindings itself, and only what came during it runs.
/// </summary>
public sealed class BindingSyncScheduler : IDisposable
{
    /// <summary>How long requests have to stop before a project is refreshed.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly IBindingRefresher _refresher;
    private readonly TimeProvider _time;
    private readonly Debouncer _quiet;
    private readonly List<(BindingRefreshTarget Target, DateTimeOffset Asked)> _pending = [];
    private (BindingRefreshTarget Target, DateTimeOffset Asked, CancellationTokenSource Cancel)? _running;
    private DateTimeOffset _buildStarted;
    private bool _building;
    private bool _pumping;
    private bool _disposed;

    /// <summary>Creates the scheduler.</summary>
    public BindingSyncScheduler(IBindingRefresher refresher, TimeProvider time)
    {
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
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
                // Asked for before the build, so the build takes it over.
                _pending.Insert(0, (running.Target, running.Asked));
                running.Cancel.Cancel();
            }
        }
    }

    /// <summary>The build is over: what it took over is up to date, and the rest runs.</summary>
    public void BuildFinished()
    {
        List<BindingRefreshTarget> covered;
        lock (_gate)
        {
            _building = false;
            covered = _pending.Where(p => p.Asked <= _buildStarted).Select(p => p.Target).ToList();
            _pending.RemoveAll(p => p.Asked <= _buildStarted);
        }
        foreach (var target in covered)
        {
            Raise(target, BindingSyncState.Ready, "Brought up to date by the build.");
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
                catch (OperationCanceledException)
                {
                    // Asked for again, stopped for a build, or dropped; whatever is still due is queued.
                }
                bool cancelled;
                bool followed;
                lock (_gate)
                {
                    cancelled = next.Cancel.IsCancellationRequested;
                    // Asked for again, or taken over by a build: another run, or the build, follows.
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
        finally
        {
            lock (_gate)
            {
                _pumping = false;
            }
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
                return null;
            }
            var (target, asked) = _pending[index];
            _pending.RemoveAt(index);
            var cancel = new CancellationTokenSource();
            _running = (target, asked, cancel);
            return (target, cancel);
        }
    }

    private void Raise(BindingRefreshTarget target, BindingSyncState state, string? message) =>
        StatusChanged?.Invoke(this, new BindingSyncStatus(target, state, message));
}
