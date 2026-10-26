using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.Locking;
using Equilibrium.Daml.Build.Core.Processes;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>
/// Makes sure the pinned generator is in its private cache before a build uses it. A cache found
/// complete before costs no lock and no process; otherwise, under the cache's lock, it is
/// checked again (another build may have just installed it), repaired inside the private home
/// only, and installed with <c>dpm install package</c> when the build may download, then held to
/// the pin file by file before it is marked ready.
/// </summary>
internal sealed class GeneratorPreparation
{
    private const int QuotedLines = 6;

    private readonly IProcessRunner _runner;
    private readonly string _locks;

    /// <summary>Creates the preparation.</summary>
    /// <param name="runner">Runs dpm.</param>
    /// <param name="locks">The folder of the build's locks, usually <see cref="LockNames.Root"/>.</param>
    public GeneratorPreparation(IProcessRunner runner, string locks)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
    }

    /// <summary>Gets the generator ready, or says why it can't be.</summary>
    /// <param name="cache">The generator's private cache.</param>
    /// <param name="dpm">The dpm to install with.</param>
    /// <param name="options">Whether the build may download, and how long it waits.</param>
    /// <param name="cancellationToken">Stops the wait or the download.</param>
    /// <returns>Null when the generator is ready; otherwise the problem to report.</returns>
    public async Task<ConfigurationError?> PrepareAsync(GeneratorCache cache, string dpm, ToolOptions options, CancellationToken cancellationToken)
    {
        if (cache is null)
        {
            throw new ArgumentNullException(nameof(cache));
        }
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (cache.IsMarkedReady && cache.Check(hashFiles: false).Status == GeneratorCacheStatus.Ready)
        {
            return null;
        }

        var name = $"{cache.Pin.Repository}:{cache.Pin.Tag}";
        FileLock held;
        try
        {
            held = await FileLock.AcquireAsync(LockNames.Generator(_locks, cache.Pin.Digest), options.LockTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ConfigurationError(
                DamlDiagnosticCodes.LockTimeout,
                $"Another build kept the cache of the C# code generator {name} locked for longer than {options.LockTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s. " +
                $"Wait for it to finish, or raise {DamlProperties.LockTimeoutSeconds.Name}.");
        }
        using (held)
        {
            var state = cache.Check(hashFiles: true);
            switch (state.Status)
            {
                case GeneratorCacheStatus.Ready:
                    cache.MarkReady();
                    return null;
                case GeneratorCacheStatus.Unsupported:
                    return new ConfigurationError(DamlDiagnosticCodes.GeneratorPinConflict, $"The C# code generator {name} can't be used: {state.Reason}");
            }
            if (options.Offline || !options.AllowToolInstall)
            {
                return options.Offline
                    ? new ConfigurationError(
                        DamlDiagnosticCodes.MissingToolOffline,
                        $"The C# code generator {name} isn't in its cache: {state.Reason} {DamlProperties.Offline.Name} is true, so the build doesn't download it. " +
                        $"Build once online with {DamlProperties.AllowToolInstall.Name} set to true.")
                    : new ConfigurationError(
                        DamlDiagnosticCodes.MissingTool,
                        $"The C# code generator {name} isn't in its cache: {state.Reason} " +
                        $"Set {DamlProperties.AllowToolInstall.Name} to true to let the build download it, once per generator version.");
            }

            // dpm doesn't check what it unpacked, so cut-short files go before it unpacks again;
            // a damaged download goes with the whole home, which is ours alone.
            DeleteFolder(state.Status == GeneratorCacheStatus.Damaged && !state.BlobsDamaged ? cache.Component : cache.Home);
            if (await InstallAsync(cache, dpm, name, options, cancellationToken).ConfigureAwait(false) is { } failed)
            {
                return failed;
            }

            var installed = cache.Check(hashFiles: true);
            if (installed.Status != GeneratorCacheStatus.Ready)
            {
                return new ConfigurationError(
                    DamlDiagnosticCodes.GeneratorPinConflict,
                    $"After installing, the cache of the C# code generator {name} still doesn't match its pin: {installed.Reason}");
            }
            cache.MarkReady();
            return null;
        }
    }

    private async Task<ConfigurationError?> InstallAsync(GeneratorCache cache, string dpm, string name, ToolOptions options, CancellationToken cancellationToken)
    {
        // Beside the homes, so it stays on our ground; its own temp folder keeps what the
        // download leaves behind out of the user's %TEMP%.
        var work = Path.Combine(Path.GetDirectoryName(cache.Home)!, "install-" + Guid.NewGuid().ToString("N"));
        var temp = Path.Combine(work, "tmp");
        Directory.CreateDirectory(temp);
        try
        {
            GeneratorWorkFolder.PrepareInstall(work, cache.Pin);
            var spec = new ProcessSpec(dpm, ["install", "package"])
            {
                WorkingDirectory = work,
                Environment = new Dictionary<string, string?>
                {
                    ["DPM_HOME"] = cache.Home,
                    ["DPM_AUTO_INSTALL"] = "true",
                    ["TEMP"] = temp,
                    ["TMP"] = temp,
                },
                Timeout = options.ProcessTimeout,
            };
            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new ConfigurationError(
                    DamlDiagnosticCodes.CancelledOrTimedOut,
                    $"Downloading the C# code generator {name} didn't finish within {options.ProcessTimeout?.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s and was stopped. " +
                    $"Raise {DamlProperties.ProcessTimeoutSeconds.Name}, or set it to 0 for no limit.");
            }
            if (result.ExitCode != 0)
            {
                var output = (result.Stdout + "\n" + result.Stderr).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
                return new ConfigurationError(
                    DamlDiagnosticCodes.MissingTool,
                    $"dpm couldn't download the C# code generator {name} (exit code {result.ExitCode}):\n" +
                    SecretRedaction.Redact(string.Join("\n", output.Reverse().Take(QuotedLines).Reverse())));
            }
            return null;
        }
        finally
        {
            DeleteFolder(work);
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
