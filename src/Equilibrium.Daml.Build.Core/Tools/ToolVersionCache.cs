using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Equilibrium.Daml.Build.Core.Tools;

// What a tool printed when asked its version, for the life of the process, keyed by the file's
// path, size and time: a build of many projects asks each tool once, and updating the tool
// changes the key. Only answers of a run that succeeded are kept.
internal sealed class ToolVersionCache
{
    private readonly ConcurrentDictionary<string, Task<(IReadOnlyList<string> Output, bool Succeeded)>> _known = new(StringComparer.OrdinalIgnoreCase);

    public static ToolVersionCache Shared { get; } = new();

    public async Task<(IReadOnlyList<string> Output, bool Succeeded)> OutputAsync(string tool, Func<Task<(IReadOnlyList<string> Output, bool Succeeded)>> ask,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = new FileInfo(tool);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var completion = new TaskCompletionSource<(IReadOnlyList<string>, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _known.GetOrAdd(key, completion.Task);
        if (ReferenceEquals(pending, completion.Task))
        {
            _ = ProbeAsync();
        }
        try
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
            await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !ReferenceEquals(pending, completion.Task))
        {
            // The build that owned the shared probe was cancelled; this build still needs an answer.
            return await OutputAsync(tool, ask, cancellationToken).ConfigureAwait(false);
        }

        async Task ProbeAsync()
        {
            try
            {
                var answer = await ask().ConfigureAwait(false);
                if (!answer.Succeeded) { Forget(); }
                completion.TrySetResult(answer);
            }
            catch (OperationCanceledException ex)
            {
                Forget();
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                Forget();
                completion.TrySetException(ex);
            }
        }
        void Forget() => ((ICollection<KeyValuePair<string, Task<(IReadOnlyList<string>, bool)>>>)_known)
            .Remove(new KeyValuePair<string, Task<(IReadOnlyList<string>, bool)>>(key, pending));
    }
}
