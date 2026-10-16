using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Daml.Build.Core.Locking;

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
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Held by another process (or another build in this one).
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"{path} was still held after {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.", ex);
            }
            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Lets go of the lock.</summary>
    public void Dispose() => _file.Dispose();
}
