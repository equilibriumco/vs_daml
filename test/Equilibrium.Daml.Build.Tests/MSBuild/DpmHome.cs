using System;
using System.Threading;
using System.Threading.Tasks;

namespace Equilibrium.Daml.Build.Tests.MSBuild;

// The user's dpm home is shared by the real-dpm tests, which run in parallel: one that may install
// an SDK into it must not overlap the one that checks a build leaves it as it found it.
internal static class DpmHome
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IDisposable Exclusive()
    {
        Gate.Wait();
        return new Release();
    }

    public static async Task<IDisposable> ExclusiveAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Gate.Release();
            }
        }
    }
}
