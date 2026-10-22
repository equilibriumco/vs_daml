using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.Core.Logging;

namespace Equilibrium.Daml.VisualStudio.Bindings;

/// <summary>
/// Refreshes a project's bindings with Visual Studio's own 64-bit MSBuild in a process of its own
/// (in the job that dies with Visual Studio): the targeted build <c>EqDamlRefreshBindings</c> in the
/// configuration the solution builds the project in. What it reports goes to the Error List.
/// </summary>
internal sealed class MSBuildBindingRefresher : IBindingRefresher
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<string, string?> Quiet = new()
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["MSBUILDDISABLENODEREUSE"] = "1",
    };

    private readonly IDamlLogger _logger;
    private readonly IProcessRunner _runner;
    private readonly BindingErrorList _errors;
    private readonly SolutionProjectConfigurations _solution;
    private readonly Action<BindingRefreshTarget, int> _filesRemoved;

    /// <param name="logger">The EQ Daml Tools: Build pane.</param>
    /// <param name="runner">Starts MSBuild.</param>
    /// <param name="errors">The Error List.</param>
    /// <param name="solution">The solution's projects and configurations.</param>
    /// <param name="filesRemoved">
    /// Told when a refresh removed generated files: IntelliSense keeps their types until the project
    /// builds or reloads.
    /// </param>
    public MSBuildBindingRefresher(IDamlLogger logger, IProcessRunner runner, BindingErrorList errors, SolutionProjectConfigurations solution, Action<BindingRefreshTarget, int> filesRemoved)
    {
        _logger = logger;
        _runner = runner;
        _errors = errors;
        _solution = solution;
        _filesRemoved = filesRemoved;
    }

    public async Task<BindingRefreshResult> RefreshAsync(BindingRefreshTarget target, CancellationToken cancellationToken)
    {
        var name = Path.GetFileNameWithoutExtension(target.Project);
        var project = (await _solution.WithBindingsAsync()).FirstOrDefault(p => DamlPath.Comparer.Equals(p.Project, target.Project));
        var configuration = project.Configuration;
        if (configuration is null)
        {
            return new BindingRefreshResult(false, $"{name} isn't loaded in the solution.");
        }
        var (installDirectory, solutionDirectory) = await _solution.EnvironmentAsync();
        var (config, platform) = BindingRefreshCommand.Configuration(configuration);
        var arguments = BindingRefreshCommand.Arguments(target, config, platform, solutionDirectory);
        _logger.Log(DamlLogCategory.Build, DamlLogLevel.Information,
            $"Refreshing the Daml bindings of {name} ({config}|{platform}{(target.TargetFramework is { } framework ? ", " + framework : "")}).");
        var list = target.TargetFramework is { } listed ? BindingConsumerReader.ListOf(target.Project, config, listed, project.IntermediatePath) : null;
        var before = list is null ? [] : GeneratedProps.Read(list);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessSpec(BindingRefreshCommand.MSBuildOf(installDirectory), arguments)
                {
                    WorkingDirectory = Path.GetDirectoryName(target.Project),
                    Environment = Quiet,
                    Timeout = Limit,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            return new BindingRefreshResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return new BindingRefreshResult(false, $"MSBuild couldn't be started: {ex.Message}");
        }

        var diagnostics = MSBuildOutput.Diagnostics(result.Stdout + "\n" + result.Stderr);
        await _errors.ShowAsync(target.Project, diagnostics).ConfigureAwait(false);
        foreach (var diagnostic in diagnostics)
        {
            _logger.Log(DamlLogCategory.Build, diagnostic.IsError ? DamlLogLevel.Error : DamlLogLevel.Warning,
                $"{diagnostic.File}({diagnostic.Line},{diagnostic.Column}): {diagnostic.Code}: {diagnostic.Message}");
        }
        _logger.Log(DamlLogCategory.Build, DamlLogLevel.Debug, result.Stdout);
        if (result.ExitCode == 0)
        {
            var after = list is null ? [] : GeneratedProps.Read(list).Select(f => f.Path);
            var removed = GeneratedProps.Removed(before.Select(f => f.Path), after);
            if (removed > 0)
            {
                _filesRemoved(target, removed);
            }
            return new BindingRefreshResult(true, null);
        }
        if (diagnostics.FirstOrDefault(d => d.IsError) is { } error)
        {
            return new BindingRefreshResult(false, $"{Path.GetFileName(error.File)}: {error.Code}: {error.Message}");
        }
        // No error MSBuild's format names: the end of its output is all there is to go by.
        _logger.Log(DamlLogCategory.Build, DamlLogLevel.Warning, $"MSBuild exited with code {result.ExitCode}; the end of its output:{Environment.NewLine}{Tail(result.Stdout + Environment.NewLine + result.Stderr, 20)}");
        return new BindingRefreshResult(false, $"MSBuild exited with code {result.ExitCode}.");
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
    }
}
