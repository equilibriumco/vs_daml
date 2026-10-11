using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Diagnostics;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Sdk;

namespace Daml.VisualStudio.Core.Build;

/// <summary>
/// Runs the dpm commands the IDE offers, one at a time: output goes to the build log as it
/// arrives, and the diagnostics replace whatever the previous command left in the store.
/// </summary>
public sealed class DamlBuildCoordinator : IDisposable
{
    private readonly DpmRunner _runner;
    private readonly Func<string?> _findDpm;
    private readonly BuildDiagnosticStore _store;
    private readonly IDamlLogger _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource _all = new();

    /// <summary>Creates a coordinator.</summary>
    /// <param name="runner">Runs dpm.</param>
    /// <param name="findDpm">Finds dpm for each run; <c>null</c> when it isn't installed.</param>
    /// <param name="store">Where the diagnostics go.</param>
    /// <param name="logger">Where the output goes (the build category).</param>
    public DamlBuildCoordinator(DpmRunner runner, Func<string?> findDpm, BuildDiagnosticStore store, IDamlLogger logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _findDpm = findDpm ?? throw new ArgumentNullException(nameof(findDpm));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The store the coordinator fills.</summary>
    public BuildDiagnosticStore Store => _store;

    /// <summary>
    /// Runs <paramref name="action"/> after any command already running. Returns whether dpm
    /// succeeded.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled, here or by <see cref="CancelAll"/>.</exception>
    public async Task<bool> RunAsync(DamlBuildAction action, CancellationToken cancellationToken)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        CancellationToken all;
        lock (_gate)
        {
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
                    $"{SdkResolutionException.MissingToolCode}: dpm wasn't found on PATH or in %APPDATA%\\dpm\\bin. Install dpm (Digital Asset's package manager), or set its path in Tools > Options > Daml.");
                return false;
            }

            _store.Clear();
            _logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, $"{action.DisplayName} started in {action.WorkingDirectory}");
            DpmRunResult result;
            try
            {
                result = await _runner.RunAsync(dpm, action.Kind, action.WorkingDirectory, action.AllPackages,
                    line => _logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, DamlDiagnosticParser.StripAnsi(line.Text)),
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Log(DamlLogCategory.Build, DamlLogLevel.Warning, $"{action.DisplayName} cancelled.");
                throw;
            }

            var diagnostics = result.Diagnostics.Where(d => !d.Hidden).Select(d => BuildDiagnostic.From(d, action.WorkingDirectory)).ToList();
            _store.Replace(diagnostics);
            var errors = diagnostics.Count(d => d.Severity == DamlDiagnosticSeverity.Error);
            var warnings = diagnostics.Count(d => d.Severity == DamlDiagnosticSeverity.Warning);
            _logger.Log(DamlLogCategory.Build, result.Succeeded ? DamlLogLevel.Information : DamlLogLevel.Warning,
                $"{action.DisplayName} {(result.Succeeded ? "succeeded" : "failed")} (exit code {result.ExitCode}, {errors} error(s), {warnings} warning(s)).");
            return result.Succeeded;
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
            old = _all;
            _all = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    /// <summary>Cancels whatever is running.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _all.Cancel();
            _all.Dispose();
        }
        _oneAtATime.Dispose();
    }
}
