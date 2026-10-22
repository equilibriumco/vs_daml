using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.Locking;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Equilibrium.Daml.Build.Tasks;

/// <summary>
/// Clean for a Daml project: removes the package's own outputs, its DAR and the record of what it
/// was built from, under the lock every build of the package takes. dpm's caches, and anything
/// else in the package's .daml folder, stay.
/// </summary>
public sealed class DamlCleanPackage : Task, ICancelableTask
{
    private CancellationTokenSource? _waiting;
    private volatile bool _cancelled;

    /// <summary>The package's <c>daml.yaml</c>.</summary>
    [Required]
    public string ManifestPath { get; set; } = "";

    /// <summary>The package's DAR.</summary>
    [Required]
    public string DarPath { get; set; } = "";

    /// <summary>Where the package's build records what the DAR was built from.</summary>
    [Required]
    public string StatePath { get; set; } = "";

    /// <summary><c>DamlLockTimeoutSeconds</c>.</summary>
    public string? LockTimeoutSeconds { get; set; }

    /// <inheritdoc />
    public void Cancel()
    {
        _cancelled = true;
        try
        {
            Volatile.Read(ref _waiting)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait ended between reading it and cancelling it.
        }
    }

    /// <inheritdoc />
    public override bool Execute()
    {
        var manifest = Path.GetFullPath(ManifestPath);
        var package = Path.GetDirectoryName(manifest)!;
        var errors = new List<ConfigurationError>();
        var timeout = ToolOptions.Read(name => name == DamlProperties.LockTimeoutSeconds.Name ? LockTimeoutSeconds : null, errors).LockTimeout;
        foreach (var error in errors)
        {
            Log.LogError(null, error.Code, null, manifest, 0, 0, 0, 0, error.Message);
        }
        if (errors.Count > 0)
        {
            return false;
        }

        using var waiting = new CancellationTokenSource();
        Volatile.Write(ref _waiting, waiting);
        if (_cancelled)
        {
            waiting.Cancel();
        }
        try
        {
            using (FileLock.Acquire(LockNames.Package(LockNames.Root(Environment.GetEnvironmentVariable), package), timeout, waiting.Token))
            {
                PackageBuildState.RemoveOutputs(Path.GetFullPath(StatePath), Path.GetFullPath(DarPath));
            }
        }
        catch (TimeoutException)
        {
            Log.LogError(null, DamlDiagnosticCodes.LockTimeout, null, manifest, 0, 0, 0, 0,
                $"Another build kept the Daml package in {package} locked for longer than {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, so it wasn't cleaned. " +
                $"Wait for it to finish, or raise {DamlProperties.LockTimeoutSeconds.Name}.");
        }
        catch (OperationCanceledException)
        {
            Log.LogError(null, DamlDiagnosticCodes.CancelledOrTimedOut, null, manifest, 0, 0, 0, 0, "Cleaning the Daml package was cancelled; nothing was removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(null, null, null, manifest, 0, 0, 0, 0, $"{DarPath} couldn't be removed ({ex.Message}).");
        }
        finally
        {
            Volatile.Write(ref _waiting, null);
        }
        return !Log.HasLoggedErrors;
    }
}
