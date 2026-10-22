using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Sdk;

namespace Equilibrium.Daml.VisualStudio.Core.Build;

/// <summary>How one dpm command run from the IDE ended.</summary>
/// <param name="Succeeded">dpm exited with 0.</param>
/// <param name="Diagnostics">What damlc reported, in editor positions; hidden ones left out.</param>
public sealed record DamlBuildRun(bool Succeeded, IReadOnlyList<BuildDiagnostic> Diagnostics)
{
    /// <summary>A run that never started, e.g. because dpm is missing.</summary>
    public static DamlBuildRun NotStarted { get; } = new(false, []);
}

/// <summary>
/// Runs the dpm commands the IDE offers, one at a time: output goes to the build log as it
/// arrives, one line per damlc log entry, and the diagnostics come back to the caller, which
/// hands them to Visual Studio's build reporting.
/// </summary>
public sealed class DamlBuildCoordinator : IDisposable
{
    private readonly DpmRunner _runner;
    private readonly Func<string?> _findDpm;
    private readonly IDamlLogger _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource _all = new();
    private bool _disposed;

    /// <summary>Creates a coordinator.</summary>
    /// <param name="runner">Runs dpm.</param>
    /// <param name="findDpm">Finds dpm for each run; <c>null</c> when it isn't installed.</param>
    /// <param name="logger">Where the output goes (the build category).</param>
    public DamlBuildCoordinator(DpmRunner runner, Func<string?> findDpm, IDamlLogger logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _findDpm = findDpm ?? throw new ArgumentNullException(nameof(findDpm));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs <paramref name="action"/> after any command already running.</summary>
    /// <exception cref="OperationCanceledException">Cancelled, here or by <see cref="CancelAll"/>.</exception>
    public async Task<DamlBuildRun> RunAsync(DamlBuildAction action, CancellationToken cancellationToken)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        CancellationToken all;
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DamlBuildCoordinator));
            }
            all = _all.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, all);
        await _oneAtATime.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var dpm = _findDpm();
            if (dpm is null)
            {
                _logger.Log(DamlLogCategory.Build, DamlLogLevel.Error,
                    $"{SdkResolutionException.MissingToolCode}: dpm wasn't found on PATH or in its home directories. Install dpm (the Daml package manager), or set its path in Tools > Options > EQ Daml Tools.");
                return DamlBuildRun.NotStarted;
            }

            _logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, $"{action.DisplayName} started in {action.WorkingDirectory}");
            // damlc writes its own log entries over several lines; each stream is put back together on its own.
            var assemblers = new Dictionary<DpmOutputChannel, DamlcLogAssembler>
            {
                [DpmOutputChannel.Stdout] = new(),
                [DpmOutputChannel.Stderr] = new(),
            };
            DpmRunResult result;
            try
            {
                result = await _runner.RunAsync(dpm, action.Kind, action.WorkingDirectory, action.AllPackages,
                    line =>
                    {
                        foreach (var entry in assemblers[line.Channel].Add(DamlDiagnosticParser.StripAnsi(line.Text)))
                        {
                            Write(entry);
                        }
                    },
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Flush(assemblers.Values);
                _logger.Log(DamlLogCategory.Build, DamlLogLevel.Warning, $"{action.DisplayName} cancelled.");
                throw;
            }
            Flush(assemblers.Values);
            if (result.UnrecognisedLines.Count > 0)
            {
                _logger.Log(DamlLogCategory.Build, DamlLogLevel.Warning,
                    $"{result.UnrecognisedLines.Count} line(s) of dpm's diagnostic output weren't understood; the Error List may be incomplete. First: {result.UnrecognisedLines[0]}");
            }

            var diagnostics = result.Diagnostics.Where(d => !d.Hidden).Select(d => BuildDiagnostic.From(d, action.WorkingDirectory)).ToList();
            var errors = diagnostics.Count(d => d.Severity == DamlDiagnosticSeverity.Error);
            var warnings = diagnostics.Count(d => d.Severity == DamlDiagnosticSeverity.Warning);
            _logger.Log(DamlLogCategory.Build, result.Succeeded ? DamlLogLevel.Information : DamlLogLevel.Warning,
                $"{action.DisplayName} {(result.Succeeded ? "succeeded" : "failed")} (exit code {result.ExitCode}, {errors} error(s), {warnings} warning(s)).");
            return new DamlBuildRun(result.Succeeded, diagnostics);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Cancels every running or waiting command, e.g. when the folder closes.</summary>
    public void CancelAll()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            old = _all;
            _all = new CancellationTokenSource();
        }
        // Not disposed: a run that read the old token just before the swap still links to it,
        // and linking to a disposed source throws something other than a cancellation.
        old.Cancel();
    }

    /// <summary>Cancels whatever is running.</summary>
    public void Dispose()
    {
        CancellationTokenSource all;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            all = _all;
        }
        all.Cancel();
        // In-flight runs still link to this source and release the semaphore. Neither owns an
        // unmanaged wait handle; let them be collected after those runs have drained.
    }

    private void Flush(IEnumerable<DamlcLogAssembler> assemblers)
    {
        foreach (var assembler in assemblers)
        {
            if (assembler.Flush() is { } entry)
            {
                Write(entry);
            }
        }
    }

    private void Write(DamlcLogRecord entry) => _logger.Log(DamlLogCategory.Build, entry.Level, entry.Text);
}
