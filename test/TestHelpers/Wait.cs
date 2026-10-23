using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Equilibrium.Daml.TestHelpers;

/// <summary>
/// Waits for what another thread does. The condition is polled until it holds; only a deadline far
/// beyond any normal run fails the test, so a slow machine makes a test slower, never red.
/// </summary>
public static class Wait
{
    /// <summary>How long a condition may take before the test fails.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>Returns once <paramref name="condition"/> holds.</summary>
    /// <param name="condition">Checked every few milliseconds, from a thread-pool thread.</param>
    /// <param name="what">What the test waits for, for the failure message.</param>
    /// <param name="deadline">How long to wait; <see cref="Deadline"/> by default.</param>
    /// <exception cref="TimeoutException">The condition didn't hold in time.</exception>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? deadline = null)
    {
        var limit = deadline ?? Deadline;
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > limit)
            {
                throw new TimeoutException($"Waited {limit.TotalSeconds:0} s for {what}.");
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
