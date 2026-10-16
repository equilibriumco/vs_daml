using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.Processes;

namespace Daml.VisualStudio.Core.Build;

/// <summary>Which of dpm's output streams a line came from.</summary>
public enum DpmOutputChannel
{
    /// <summary>Standard output.</summary>
    Stdout,

    /// <summary>Standard error, where dpm writes its progress and the diagnostics.</summary>
    Stderr,
}

/// <summary>One line of dpm output, without its line break.</summary>
/// <param name="Channel">Where it was written.</param>
/// <param name="Text">The line.</param>
public readonly record struct DpmOutputLine(DpmOutputChannel Channel, string Text);

/// <summary>How a dpm command ended.</summary>
/// <param name="ExitCode">dpm's exit code.</param>
/// <param name="Diagnostics">Every diagnostic block it printed, in output order.</param>
public sealed record DpmRunResult(int ExitCode, IReadOnlyList<DamlDiagnostic> Diagnostics)
{
    /// <summary>True when dpm exited with 0.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>Lines inside diagnostic blocks that weren't understood (see <see cref="DamlDiagnosticParser.UnrecognisedLines"/>).</summary>
    public IReadOnlyList<string> UnrecognisedLines { get; init; } = [];
}

/// <summary>
/// Runs <c>dpm build</c>, <c>test</c> or <c>clean</c> in a package or multi-package folder,
/// handing over the output as it arrives and collecting the diagnostics on the way.
/// </summary>
public sealed class DpmRunner
{
    private readonly IProcessRunner _runner;

    /// <summary>Creates a runner starting dpm through <paramref name="runner"/>.</summary>
    public DpmRunner(IProcessRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>Runs one command to completion.</summary>
    /// <param name="dpmPath">Full path of dpm (the <c>.cmd</c> wrapper is fine).</param>
    /// <param name="kind">What to run.</param>
    /// <param name="workingDirectory">The package folder, or the multi-package folder with <paramref name="allPackages"/>.</param>
    /// <param name="allPackages">Run on every package of the folder's multi-package.yaml.</param>
    /// <param name="onOutput">
    /// Gets every output line as it arrives, one call at a time, on a background thread;
    /// lines of each stream keep their order.
    /// </param>
    /// <param name="cancellationToken">Cancelling kills dpm and everything it started.</param>
    /// <exception cref="ArgumentException">The combination isn't something dpm can do (see <see cref="DpmCommand.Arguments"/>).</exception>
    public async Task<DpmRunResult> RunAsync(
        string dpmPath,
        DpmCommandKind kind,
        string workingDirectory,
        bool allPackages,
        Action<DpmOutputLine>? onOutput,
        CancellationToken cancellationToken)
    {
        var arguments = DpmCommand.Arguments(kind, allPackages);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = _runner.Start(new ProcessSpec(dpmPath, arguments) { WorkingDirectory = workingDirectory });
        using var kill = cancellationToken.Register(process.KillTree);
        process.StandardInput.Close();

        var gate = new object();
        var stdout = new DamlDiagnosticParser();
        var stderr = new DamlDiagnosticParser();
        await Task.WhenAll(
            PumpAsync(process.StandardOutput, DpmOutputChannel.Stdout, stdout, onOutput, gate),
            PumpAsync(process.StandardError, DpmOutputChannel.Stderr, stderr, onOutput, gate)).ConfigureAwait(false);
        var exitCode = await process.Exited.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new DpmRunResult(exitCode, stdout.Diagnostics.Concat(stderr.Diagnostics).ToList())
        {
            UnrecognisedLines = stdout.UnrecognisedLines.Concat(stderr.UnrecognisedLines).ToList(),
        };
    }

    private static async Task PumpAsync(Stream stream, DpmOutputChannel kind, DamlDiagnosticParser parser, Action<DpmOutputLine>? onOutput, object gate)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            parser.AddLine(line);
            if (onOutput is not null)
            {
                lock (gate)
                {
                    onOutput(new DpmOutputLine(kind, line));
                }
            }
        }
        parser.Complete();
    }
}
