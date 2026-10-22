using System;

namespace Equilibrium.Daml.VisualStudio.Core.Concurrency;

/// <summary>Publishes background results only while the input generation that produced them is current.</summary>
/// <typeparam name="T">An immutable result shared with readers.</typeparam>
public sealed class LatestResult<T> : IDisposable where T : class
{
    private readonly object _gate = new();
    private long _generation;
    private T? _current;
    private bool _disposed;

    /// <summary>The generation to capture before starting work.</summary>
    public long Generation { get { lock (_gate) { return _generation; } } }

    /// <summary>The last accepted result, retained while its replacement is computed.</summary>
    public T? Current { get { lock (_gate) { return _current; } } }

    /// <summary>Invalidates outstanding work when its input changes.</summary>
    public void Invalidate()
    {
        lock (_gate) { _generation++; }
    }

    /// <summary>Accepts a result only if its input is still current and the owner is still alive.</summary>
    public bool TryPublish(long generation, T result)
    {
        if (result is null) { throw new ArgumentNullException(nameof(result)); }
        lock (_gate)
        {
            if (_disposed || generation != _generation) { return false; }
            _current = result;
            return true;
        }
    }

    /// <summary>Releases the retained result and rejects work still running.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current = null;
            _generation++;
        }
    }
}
