using System;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;

namespace Daml.VisualStudio.Core.Bindings;

/// <summary>
/// Keeps a <see cref="BindingIndex"/> up to date with a solution that keeps changing. Changes are
/// gathered until things have been quiet for the debounce, then read in one go off the caller's
/// thread. A change that arrives while a read runs cancels it, since it reads a solution that is
/// already old; its changes are read again with the new ones. Every finished read is published as
/// a new index; a failed one leaves the index as it was and keeps its changes for the next read.
/// </summary>
public sealed class BindingIndexUpdates : IDisposable
{
    private readonly Func<BindingIndex, IndexChanges, CancellationToken, Task<BindingIndex>> _read;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly IDamlLogger _logger;
    private readonly object _gate = new();
    private IndexChanges _pending = IndexChanges.None;
    private BindingIndex _current = BindingIndex.Empty;
    private ITimer? _timer;
    private CancellationTokenSource? _running;
    private bool _startWhenDone;
    private bool _disposed;

    /// <summary>Creates the updates, starting from an empty index.</summary>
    /// <param name="read">Reads changes into an index and returns the new one; observes the token.</param>
    /// <param name="time">The clock the debounce waits on.</param>
    /// <param name="debounce">How long things have to be quiet before a read.</param>
    /// <param name="logger">Where failed reads are reported.</param>
    public BindingIndexUpdates(Func<BindingIndex, IndexChanges, CancellationToken, Task<BindingIndex>> read, TimeProvider time, TimeSpan debounce, IDamlLogger logger)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _debounce = debounce;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised on a thread-pool thread with every new index.</summary>
    public event EventHandler<BindingIndex>? Published;

    /// <summary>The latest published index.</summary>
    public BindingIndex Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Asks for <paramref name="changes"/> to be read, once things have been quiet for the debounce.</summary>
    public void Request(IndexChanges changes)
    {
        lock (_gate)
        {
            if (_disposed || (changes ?? throw new ArgumentNullException(nameof(changes))).IsEmpty)
            {
                return;
            }
            _pending = _pending.Merge(changes);
            _running?.Cancel();
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Start(), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Cancels the read in progress; later requests are ignored.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _running?.Cancel();
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Start()
    {
        IndexChanges changes;
        BindingIndex from;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed || _pending.IsEmpty)
            {
                return;
            }
            if (_running is not null)
            {
                // The cancelled read is still winding down; it starts the next one when it ends.
                _startWhenDone = true;
                return;
            }
            (changes, _pending) = (_pending, IndexChanges.None);
            from = _current;
            cancellation = new CancellationTokenSource();
            _running = cancellation;
        }
        _ = ReadAsync(from, changes, cancellation);
    }

    private async Task ReadAsync(BindingIndex from, IndexChanges changes, CancellationTokenSource cancellation)
    {
        BindingIndex? read = null;
        Exception? failure = null;
        try
        {
            read = await Task.Run(() => _read(from, changes, cancellation.Token), cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer change came in; these changes are read again with it.
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        bool publish;
        bool startNext;
        lock (_gate)
        {
            publish = read is not null && !cancellation.IsCancellationRequested && !_disposed;
            if (publish)
            {
                _current = read!;
            }
            else if (!_disposed)
            {
                _pending = changes.Merge(_pending);
            }
            _running = null;
            startNext = _startWhenDone && !_disposed;
            _startWhenDone = false;
        }
        cancellation.Dispose();

        if (failure is not null)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, "Reading the Daml bindings of the solution failed; they stay as they were until the next change.", failure);
        }
        if (publish)
        {
            Published?.Invoke(this, read!);
        }
        if (startNext)
        {
            Start();
        }
    }
}
