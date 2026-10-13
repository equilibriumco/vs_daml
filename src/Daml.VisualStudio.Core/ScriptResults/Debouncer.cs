using System;
using System.Threading;
using System.Threading.Tasks;

namespace Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// Lets the last of a burst of calls through once things have been quiet for a while. Every
/// <see cref="WaitAsync"/> replaces the one before it; the replaced wait ends at once with
/// <c>false</c>, the survivor ends with <c>true</c> after the delay.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _delay;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    /// <summary>Creates a debouncer.</summary>
    public Debouncer(TimeSpan delay, TimeProvider time)
    {
        _delay = delay;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Waits out the delay; <c>false</c> if a newer wait or <see cref="Dispose"/> came first.</summary>
    public async Task<bool> WaitAsync()
    {
        CancellationTokenSource mine;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
            _pending?.Cancel();
            _pending?.Dispose();
            mine = new CancellationTokenSource();
            _pending = mine;
        }
        try
        {
            await _time.Delay(_delay, mine.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        lock (_gate)
        {
            return !_disposed && ReferenceEquals(_pending, mine);
        }
    }

    /// <summary>Ends the waiting call, if any, with <c>false</c>; later calls return <c>false</c> at once.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
