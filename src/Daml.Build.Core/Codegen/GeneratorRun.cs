using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Processes;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// Runs the pinned generator for one generation into a staging folder, then writes the
/// generation's manifest there, so that the staging folder is the whole generation the
/// publisher puts in place. The generator runs from its private home and never installs
/// anything; its wrapper looks for java on PATH only, so the JDK the preflight checked goes
/// first on it. What it leaves in its temp folder stays in the run folder, which goes with it.
/// </summary>
public sealed class GeneratorRun
{
    private const int QuotedLines = 6;

    // JDK and SLF4J print these on every run, failed or not.
    private static readonly string[] Noise = ["WARNING:", "SLF4J"];

    private readonly IProcessRunner _runner;
    private readonly Func<string, string?> _environment;

    /// <summary>Creates the run.</summary>
    /// <param name="runner">Runs dpm.</param>
    /// <param name="environment">Environment variables by name; PATH is extended for the generator.</param>
    public GeneratorRun(IProcessRunner runner, Func<string, string?> environment)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>Generates <paramref name="generation"/> into <paramref name="staging"/>.</summary>
    /// <param name="tools">The checked tools and the ready generator.</param>
    /// <param name="generation">What to generate and from which DAR.</param>
    /// <param name="staging">An empty folder from <see cref="GenerationPublisher.NewStaging"/>.</param>
    /// <param name="owner">The canonical path of the project the generation belongs to.</param>
    /// <param name="timeout">How long the generator may run; null for no limit.</param>
    /// <param name="cancellationToken">Stops the generator and everything it started.</param>
    /// <returns>Null when the staging folder holds the complete generation; otherwise why not.</returns>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public async Task<ConfigurationError?> RunAsync(
        CodegenTools tools,
        PlannedGeneration generation,
        string staging,
        string owner,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }
        if (generation is null)
        {
            throw new ArgumentNullException(nameof(generation));
        }
        var reference = generation.Input.Reference;
        var dar = generation.Input.DarPath;
        DarProvenance package;
        try
        {
            package = DarProvenance.Read(dar);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ConfigurationError(DamlDiagnosticCodes.CodegenFailed, $"No C# can be generated for {reference.Include}: {ex.Message}");
        }
        // Recovery removes it with the staging when a run dies.
        var run = GenerationPublisher.RunFolderOf(staging);
        var temp = Path.Combine(run, "tmp");
        Directory.CreateDirectory(temp);
        try
        {
            GeneratorWorkFolder.PrepareRun(run, tools.Cache.Pin);
            var arguments = new List<string> { "codegen-cs", "--dar", dar, "--out", staging };
            if (reference.Namespace is { } @namespace)
            {
                arguments.AddRange(["--", "-n", @namespace]);
            }
            var javaBin = Path.GetDirectoryName(tools.Java)!;
            var path = _environment("PATH");
            var spec = new ProcessSpec(tools.Dpm, arguments)
            {
                WorkingDirectory = run,
                Environment = new Dictionary<string, string?>
                {
                    ["DPM_HOME"] = tools.Cache.Home,
                    ["DPM_AUTO_INSTALL"] = "false",
                    ["PATH"] = string.IsNullOrEmpty(path) ? javaBin : javaBin + ";" + path,
                    ["JAVA_HOME"] = Path.GetDirectoryName(javaBin),
                    ["TEMP"] = temp,
                    ["TMP"] = temp,
                },
                Timeout = timeout,
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
                    $"dpm codegen-cs for {reference.Include} didn't finish within {timeout?.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s and was stopped. " +
                    $"Raise {DamlProperties.ProcessTimeoutSeconds.Name}, or set it to 0 for no limit.");
            }
            if (result.ExitCode != 0)
            {
                var lines = (result.Stdout + "\n" + result.Stderr).Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !Noise.Any(n => l.StartsWith(n, StringComparison.Ordinal)))
                    .ToList();
                return new ConfigurationError(
                    DamlDiagnosticCodes.CodegenFailed,
                    $"dpm codegen-cs failed for {reference.Include} (exit code {result.ExitCode}):\n" +
                    SecretRedaction.Redact(string.Join("\n", lines.Skip(Math.Max(0, lines.Count - QuotedLines)))));
            }

            try
            {
                return WriteManifest(tools, generation, package, staging, owner);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ConfigurationError(
                    DamlDiagnosticCodes.CodegenFailed,
                    $"dpm codegen-cs ran for {reference.Include}, but what it wrote to {staging} couldn't be read back: {ex.Message}");
            }
        }
        finally
        {
            try
            {
                LongPaths.DeleteFolder(run);
            }
            catch (IOException)
            {
                // Something may still hold a file in it; the next recovery takes it with the stagings.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }
    }

    private static ConfigurationError? WriteManifest(CodegenTools tools, PlannedGeneration generation, DarProvenance package, string staging, string owner)
    {
        var reference = generation.Input.Reference;
        var dar = generation.Input.DarPath;
        var files = LongPaths.RelativeFiles(staging)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!files.Any(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            return new ConfigurationError(DamlDiagnosticCodes.CodegenFailed, $"dpm codegen-cs wrote no C# for {reference.Include} from {dar}.");
        }
        new GenerationManifest(
            reference.GenerationId,
            generation.Fingerprint,
            owner,
            reference.Kind,
            reference.Path,
            reference.Namespace,
            dar,
            Hashing.Sha256File(dar),
            tools.Cache.Pin.Reference,
            files.Select(f => new GenerationManifestFile(f, Hashing.Sha256File(Path.Combine(staging, f)))).ToList())
        {
            Package = package,
            Source = generation.Input.Source,
        }
            .Write(Path.Combine(staging, GenerationManifest.FileName));
        return null;
    }
}
