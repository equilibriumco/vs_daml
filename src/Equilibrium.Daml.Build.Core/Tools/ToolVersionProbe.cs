using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Processes;

namespace Equilibrium.Daml.Build.Core.Tools;

/// <summary>
/// Asks a tool its version through an <see cref="IProcessRunner"/>, once per file for the life of
/// the process, so a build of many projects starts one process per tool.
/// </summary>
public sealed class ToolVersionProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly IProcessRunner _runner;
    private readonly ToolVersionCache _versions;

    /// <summary>Creates a probe that shares what it learns with every other probe in the process.</summary>
    public ToolVersionProbe(IProcessRunner runner)
        : this(runner, ToolVersionCache.Shared)
    {
    }

    internal ToolVersionProbe(IProcessRunner runner, ToolVersionCache versions)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
    }

    /// <summary>The version of the dpm at <paramref name="dpm"/>, and why the build can't use it, or null when it can.</summary>
    /// <param name="dpm">The dpm to ask.</param>
    /// <param name="environment">Variables to set for it; null for none.</param>
    /// <param name="cancellationToken">Stops waiting, and the probe if this caller started it.</param>
    public async Task<(Version? Version, ConfigurationError? Problem)> DpmAsync(string dpm, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        var (output, _) = await AskAsync(dpm, DpmVersion.Arguments, stderr: false, environment, cancellationToken).ConfigureAwait(false);
        var version = DpmVersion.Parse(output);
        return (version, DpmVersion.Problem(version, output));
    }

    // A tool that can't run says so in its output, which the version check then quotes.
    internal Task<(IReadOnlyList<string> Output, bool Succeeded)> AskAsync(
        string tool, IReadOnlyList<string> arguments, bool stderr, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
        _versions.OutputAsync(tool, async () =>
        {
            try
            {
                var spec = new ProcessSpec(tool, arguments) { Timeout = ProbeTimeout };
                if (environment is not null)
                {
                    spec = spec with { Environment = environment };
                }
                var result = await _runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
                var text = stderr ? result.Stderr + "\n" + result.Stdout : result.Stdout + "\n" + result.Stderr;
                return ((IReadOnlyList<string>)text.Split('\n').Select(l => l.TrimEnd('\r')).ToList(), result.ExitCode == 0);
            }
            catch (Exception ex) when (ex is TimeoutException or FileNotFoundException)
            {
                return ([ex.Message], false);
            }
        }, cancellationToken);
}
