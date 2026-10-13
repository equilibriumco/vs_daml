using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Daml.VisualStudio.Core.Processes;

/// <summary>
/// The only way the extension starts external programs (dpm, daml, damlc, java), so that
/// everything above it can be tested without them.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs a program to completion with its input closed and collects its output. On timeout
    /// the whole process tree is killed and <see cref="TimeoutException"/> is thrown; on
    /// cancellation the tree is killed and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    /// <exception cref="FileNotFoundException">The program doesn't exist.</exception>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);

    /// <summary>Starts a long-running program and hands over its standard streams.</summary>
    /// <exception cref="FileNotFoundException">The program doesn't exist.</exception>
    /// <exception cref="ArgumentException">The program isn't given by a full path.</exception>
    IRunningProcess Start(ProcessSpec spec);
}

/// <summary>
/// A started program. Disposing it kills the program and everything it started (they live in a
/// kill-on-close job) and releases the handles; <see cref="Exited"/> is cancelled if it hadn't
/// completed.
/// </summary>
public interface IRunningProcess : IDisposable
{
    /// <summary>Process ID.</summary>
    int Id { get; }

    /// <summary>The program's standard input; dispose it to signal end of input.</summary>
    Stream StandardInput { get; }

    /// <summary>The program's standard output.</summary>
    Stream StandardOutput { get; }

    /// <summary>The program's standard error.</summary>
    Stream StandardError { get; }

    /// <summary>Completes with the exit code once the program has exited.</summary>
    Task<int> Exited { get; }

    /// <summary>Kills the program and every process it started, without waiting.</summary>
    void KillTree();
}
