using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Daml.Build.Core;
using Daml.Build.Core.Building;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Locking;
using Daml.Build.Core.Tools;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

/// <summary>
/// Runs one dpm command in a package's folder and reports what it printed the way MSBuild
/// shows compiler output: damlc's diagnostics at their files and ranges, a missing SDK with the
/// way to get it, and a failure nothing explains with the end of the output. dpm installs
/// nothing unless the build allows it. A timeout or a cancelled build stops dpm and every
/// process it started.
/// </summary>
public abstract class DpmToolTask : ToolTask
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private DpmOutputReport? _report;
    private string? _dpm;
    private TimeSpan _lockTimeout;
    private CancellationTokenSource? _waiting;
    private volatile bool _cancelled;

    /// <summary>The package's <c>daml.yaml</c>; dpm runs in its folder.</summary>
    [Required]
    public string ManifestPath { get; set; } = "";

    /// <summary><c>DamlDpmPath</c>.</summary>
    public string? DpmPath { get; set; }

    /// <summary><c>DamlOffline</c>.</summary>
    public string? Offline { get; set; }

    /// <summary><c>DamlAllowToolInstall</c>.</summary>
    public string? AllowToolInstall { get; set; }

    /// <summary><c>DamlProcessTimeoutSeconds</c>.</summary>
    public string? ProcessTimeoutSeconds { get; set; }

    /// <summary><c>DamlSkipPreflight</c>: don't check dpm's version.</summary>
    public string? SkipPreflight { get; set; }

    /// <summary><c>DamlLockTimeoutSeconds</c>: how long to wait for another build of the same package.</summary>
    public string? LockTimeoutSeconds { get; set; }

    /// <summary>The dpm command this task runs.</summary>
    protected abstract DpmCommandKind Command { get; }

    /// <summary>The full path of the manifest.</summary>
    protected string FullManifestPath => Path.GetFullPath(ManifestPath);

    /// <inheritdoc />
    protected override string ToolName => "dpm";

    /// <inheritdoc />
    protected override Encoding StandardOutputEncoding => Utf8;

    /// <inheritdoc />
    protected override Encoding StandardErrorEncoding => Utf8;

    /// <inheritdoc />
    public override bool Execute() => base.Execute() && !Log.HasLoggedErrors;

    /// <inheritdoc />
    public override void Cancel()
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
        base.Cancel();
    }

    /// <inheritdoc />
    protected override string GenerateFullPathToTool() => _dpm ?? ToolName;

    /// <inheritdoc />
    protected override string GenerateCommandLineCommands() => string.Join(" ", DpmCommand.Arguments(Command, allPackages: false));

    /// <inheritdoc />
    protected override string GetWorkingDirectory() => Path.GetDirectoryName(FullManifestPath)!;

    /// <inheritdoc />
    protected override bool ValidateParameters()
    {
        var properties = new Dictionary<string, string?>
        {
            [DamlProperties.DpmPath.Name] = DpmPath,
            [DamlProperties.Offline.Name] = Offline,
            [DamlProperties.AllowToolInstall.Name] = AllowToolInstall,
            [DamlProperties.ProcessTimeoutSeconds.Name] = ProcessTimeoutSeconds,
            [DamlProperties.SkipPreflight.Name] = SkipPreflight,
            [DamlProperties.LockTimeoutSeconds.Name] = LockTimeoutSeconds,
        };
        var errors = new List<ConfigurationError>();
        var options = ToolOptions.Read(name => properties.TryGetValue(name, out var value) ? value : null, errors);
        var project = BuildEngine.ProjectFileOfTaskNode;
        if (errors.Count == 0)
        {
            var projectFolder = string.IsNullOrEmpty(project) ? GetWorkingDirectory() : Path.GetDirectoryName(Path.GetFullPath(project))!;
            _dpm = DpmLocation.Find(options.DpmPath, projectFolder, Environment.GetEnvironmentVariable, new PhysicalFileSystem(), errors);
        }
        foreach (var error in errors)
        {
            Log.LogError(null, error.Code, null, project, 0, 0, 0, 0, error.Message);
        }
        if (errors.Count > 0)
        {
            return false;
        }

        Timeout = options.ProcessTimeout is { } limit ? (int)Math.Min(int.MaxValue, limit.TotalMilliseconds) : System.Threading.Timeout.Infinite;
        _lockTimeout = options.LockTimeout;
        // Last, so the build's own setting wins over one passed in or inherited.
        EnvironmentVariables = (EnvironmentVariables ?? []).Concat(options.DpmEnvironment.Select(v => $"{v.Key}={v.Value}")).ToArray();
        if (!DpmIsSupported(options.SkipPreflight, project))
        {
            return false;
        }
        _report = new DpmOutputReport(Command, GetWorkingDirectory(), FullManifestPath, options);
        return true;
    }

    private bool DpmIsSupported(bool skip, string project)
    {
        if (skip)
        {
            Log.LogWarning(null, DamlDiagnosticCodes.DpmTooOld, null, project, 0, 0, 0, 0,
                $"{DamlProperties.SkipPreflight.Name} is true, so the build doesn't check that dpm is new enough.");
            return true;
        }
        var (version, output) = DpmVersionProbe.Read(_dpm!, BuildEngine, EnvironmentVariables);
        if (DpmVersion.Problem(version, output) is not { } problem)
        {
            return true;
        }
        Log.LogError(null, problem.Code, null, project, 0, 0, 0, 0, problem.Message);
        return false;
    }

    /// <inheritdoc />
    protected override void LogEventsFromTextOutput(string singleLine, MessageImportance messageImportance)
    {
        // dpm's own words go to the detailed log; the report decides what becomes an error. The one
        // line per script of a test run is what -t:DamlTest is for, so it shows at any verbosity.
        if (Command == DpmCommandKind.Test && ScriptResultLine.TryParse(singleLine) is { } result)
        {
            Log.LogMessage(MessageImportance.High, result);
        }
        else
        {
            Log.LogMessage(MessageImportance.Low, singleLine);
        }
        _report?.AddLine(singleLine);
    }

    /// <inheritdoc />
    protected override int ExecuteTool(string pathToTool, string responseFileCommands, string commandLineCommands)
    {
        // dpm builds the package in place, in the one .daml folder every configuration shares.
        if (TakePackageLock() is not { } held)
        {
            return -1;
        }
        using (held)
        {
            return RunDpm(pathToTool, responseFileCommands, commandLineCommands);
        }
    }

    private FileLock? TakePackageLock()
    {
        var verb = Command.ToString().ToLowerInvariant();
        var package = GetWorkingDirectory();
        using var waiting = new CancellationTokenSource();
        Volatile.Write(ref _waiting, waiting);
        if (_cancelled)
        {
            waiting.Cancel();
        }
        try
        {
            return FileLock.Acquire(LockNames.Package(LockNames.Root(Environment.GetEnvironmentVariable), package), _lockTimeout, waiting.Token);
        }
        catch (TimeoutException)
        {
            Log.LogError(null, DamlDiagnosticCodes.LockTimeout, null, FullManifestPath, 0, 0, 0, 0,
                $"Another build kept the Daml package in {package} locked for longer than {_lockTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, so dpm {verb} didn't run. " +
                $"Wait for it to finish, or raise {DamlProperties.LockTimeoutSeconds.Name}.");
            return null;
        }
        catch (OperationCanceledException)
        {
            Stopped($"dpm {verb} was cancelled while it waited for another build of the package.");
            return null;
        }
        finally
        {
            Volatile.Write(ref _waiting, null);
        }
    }

    private int RunDpm(string pathToTool, string responseFileCommands, string commandLineCommands)
    {
        var clock = Stopwatch.StartNew();
        var exitCode = base.ExecuteTool(pathToTool, responseFileCommands, commandLineCommands);
        var verb = Command.ToString().ToLowerInvariant();
        if (_cancelled)
        {
            Stopped($"dpm {verb} was cancelled; it was stopped with every process it started.");
        }
        else if (Timeout != System.Threading.Timeout.Infinite && exitCode == -1 && clock.ElapsedMilliseconds >= Timeout)
        {
            Stopped($"dpm {verb} didn't finish within {Timeout / 1000} s and was stopped with every process it started. " +
                $"Raise {DamlProperties.ProcessTimeoutSeconds.Name}, or set it to 0 for no limit.");
        }
        else
        {
            var outcome = _report!.Complete(exitCode);
            foreach (var message in outcome.Messages)
            {
                Log.Report(message);
            }
            Completed(outcome, exitCode);
        }
        return exitCode;
    }

    /// <summary>Called once dpm ended on its own, after its messages were logged.</summary>
    protected virtual void Completed(DpmOutcome outcome, int exitCode)
    {
    }

    // The errors from dpm's output already explain the exit code; MSBuild's own "exited with
    // code 1" would only repeat it.
    /// <inheritdoc />
    protected override bool HandleTaskExecutionErrors() => !Log.HasLoggedErrors && base.HandleTaskExecutionErrors();

    private void Stopped(string message) =>
        Log.LogError(null, DamlDiagnosticCodes.CancelledOrTimedOut, null, FullManifestPath, 0, 0, 0, 0, message);
}
