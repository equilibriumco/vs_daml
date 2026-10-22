using System;
using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Supervision;

/// <summary>What to do after a crash.</summary>
/// <param name="OpenCircuit">Stop restarting until the user asks.</param>
/// <param name="Delay">How long to wait before the next start, when the circuit stays closed.</param>
public readonly record struct RestartDecision(bool OpenCircuit, TimeSpan Delay);

/// <summary>
/// Backoff and circuit breaker for server crashes. VS itself restarts a crashed server at once
/// and without limit, so this is the only thing standing between a server that dies on startup
/// and a restart loop.
/// </summary>
public sealed class RestartPolicy
{
    private readonly IReadOnlyList<TimeSpan> _delays;
    private readonly int _maxCrashes;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _crashes = new();

    /// <summary>1, 2, 4, 8, 16 seconds; more than 5 crashes within 3 minutes opens the circuit.</summary>
    public RestartPolicy()
        : this([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)], 5, TimeSpan.FromMinutes(3))
    {
    }

    /// <summary>Creates a policy.</summary>
    /// <param name="delays">Delay after the 1st, 2nd, ... crash in the window; the last one repeats.</param>
    /// <param name="maxCrashes">Crashes tolerated within <paramref name="window"/>; one more opens the circuit.</param>
    /// <param name="window">How long a crash counts.</param>
    public RestartPolicy(IReadOnlyList<TimeSpan> delays, int maxCrashes, TimeSpan window)
    {
        if (delays is null || delays.Count == 0)
        {
            throw new ArgumentException("At least one delay is needed.", nameof(delays));
        }
        _delays = delays;
        _maxCrashes = maxCrashes;
        _window = window;
    }

    /// <summary>Records a crash at <paramref name="now"/> and decides what follows.</summary>
    public RestartDecision RecordCrash(DateTimeOffset now)
    {
        while (_crashes.Count > 0 && now - _crashes.Peek() > _window)
        {
            _crashes.Dequeue();
        }
        _crashes.Enqueue(now);
        if (_crashes.Count > _maxCrashes)
        {
            return new RestartDecision(true, TimeSpan.Zero);
        }
        return new RestartDecision(false, _delays[Math.Min(_crashes.Count, _delays.Count) - 1]);
    }

    /// <summary>Forgets every crash, e.g. when the user restarts by hand.</summary>
    public void Reset() => _crashes.Clear();
}
