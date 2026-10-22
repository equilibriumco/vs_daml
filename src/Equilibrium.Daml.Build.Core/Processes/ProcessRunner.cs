using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Equilibrium.Daml.Build.Core.Processes;

/// <summary>
/// <see cref="IProcessRunner"/> that creates every process inside its own kill-on-close Job
/// Object, so nothing it starts can outlive it or the host.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly Action<StartStage, int>? _startHook;

    /// <summary>Creates a runner.</summary>
    public ProcessRunner()
    {
    }

    // Lets tests fail a start at a given point to prove nothing is left behind.
    internal ProcessRunner(Action<StartStage, int>? startHook) => _startHook = startHook;

    internal enum StartStage
    {
        JobCreated,
        Created,
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        if (spec is null)
        {
            throw new ArgumentNullException(nameof(spec));
        }
        cancellationToken.ThrowIfCancellationRequested();

        using var process = JobProcess.Start(spec, _startHook);
        process.StandardInput.Dispose();
        var stdout = ReadAllAsync(process.StandardOutput);
        var stderr = ReadAllAsync(process.StandardError);
        var finished = Task.WhenAll(process.Exited, stdout, stderr);

        var limit = spec.Timeout;
        using var timeout = limit is { } span ? new CancellationTokenSource(span) : new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stopped = Task.Delay(Timeout.Infinite, stop.Token);
        if (await Task.WhenAny(finished, stopped).ConfigureAwait(false) != finished)
        {
            process.KillTree();
            // The reads end once the pipes close behind the dead tree; let them finish here
            // rather than fault, unobserved, when the pipes are disposed under them.
            await Task.WhenAll(stdout, stderr).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{spec.FileName} didn't finish within {limit?.TotalSeconds:0.#} s and was stopped.");
        }

        return new ProcessResult(await process.Exited.ConfigureAwait(false), await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <inheritdoc />
    public IRunningProcess Start(ProcessSpec spec) =>
        JobProcess.Start(spec ?? throw new ArgumentNullException(nameof(spec)), _startHook);

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}
