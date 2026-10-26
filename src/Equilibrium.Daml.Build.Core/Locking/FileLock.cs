using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Equilibrium.Daml.Build.Core.Locking;

/// <summary>
/// A lock between processes: a file held open with no sharing. It belongs to no thread, so it
/// can be held across awaits and handed between MSBuild's threads, and Windows lets go of it
/// when the holding process dies, so a killed build never leaves it taken. What that build
/// left half done is for whoever takes the lock next to recover.
/// </summary>
public sealed class FileLock : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private readonly FileStream _file;

    private FileLock(FileStream file) => _file = file;

    /// <summary>Takes the lock at <paramref name="path"/>, waiting for another holder to let go.</summary>
    /// <param name="path">The lock file; its folder is made if needed.</param>
    /// <param name="timeout">How long to wait for another holder.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="TimeoutException">Another holder kept it longer than <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public static async Task<FileLock> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Prepare(path, timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryTake(path, timeout, deadline) is { } taken)
            {
                return taken;
            }
            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the lock at <paramref name="path"/> like <see cref="AcquireAsync"/>, waiting on the
    /// calling thread: for a tool task, which MSBuild runs on a worker thread of its own.
    /// </summary>
    /// <exception cref="TimeoutException">Another holder kept it longer than <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public static FileLock Acquire(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Prepare(path, timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryTake(path, timeout, deadline) is { } taken)
            {
                return taken;
            }
            cancellationToken.WaitHandle.WaitOne(Poll);
        }
    }

    private static DateTime Prepare(string path, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path ?? throw new ArgumentNullException(nameof(path)))!);
        return DateTime.UtcNow + timeout;
    }

    private static FileLock? TryTake(string path, TimeSpan timeout, DateTime deadline)
    {
        try
        {
            return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex) when (IsHeld(ex) && DateTime.UtcNow < deadline)
        {
            // Held by another process (or another build in this one).
            return null;
        }
        catch (IOException ex) when (IsHeld(ex))
        {
            throw new TimeoutException($"{path} was still held after {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.", ex);
        }
    }

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION. Any other IOException (a bad path, a full
    // disk) won't clear by waiting and goes to the caller as it is.
    private static bool IsHeld(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>Lets go of the lock.</summary>
    public void Dispose() => _file.Dispose();
}
