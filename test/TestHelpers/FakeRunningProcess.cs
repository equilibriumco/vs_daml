using System.IO;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Processes;

namespace Daml.TestHelpers;

/// <summary>
/// <see cref="IRunningProcess"/> whose exit a test triggers by hand. Killing or disposing it
/// ends it the way closing a real process's job would.
/// </summary>
public sealed class FakeRunningProcess : IRunningProcess
{
    private static int s_nextId = 50_000;

    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a fake that runs until told otherwise.</summary>
    public FakeRunningProcess(ProcessSpec spec)
    {
        Spec = spec;
        Id = System.Threading.Interlocked.Increment(ref s_nextId);
    }

    /// <summary>What it was started with.</summary>
    public ProcessSpec Spec { get; }

    /// <inheritdoc />
    public int Id { get; }

    /// <inheritdoc />
    public Stream StandardInput { get; } = new MemoryStream();

    /// <inheritdoc />
    public Stream StandardOutput { get; } = new MemoryStream();

    /// <inheritdoc />
    public Stream StandardError { get; } = new MemoryStream();

    /// <inheritdoc />
    public Task<int> Exited => _exited.Task;

    /// <summary>Whether <see cref="KillTree"/> was called.</summary>
    public bool Killed { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> was called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Ends the process with <paramref name="exitCode"/>, as a crash or a normal exit would.</summary>
    public void Exit(int exitCode) => _exited.TrySetResult(exitCode);

    /// <inheritdoc />
    public void KillTree()
    {
        Killed = true;
        Exit(1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Disposed = true;
        _exited.TrySetCanceled();
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
    }
}
