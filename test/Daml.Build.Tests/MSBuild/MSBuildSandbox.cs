using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Daml.TestHelpers;

namespace Daml.Build.Tests.MSBuild;

/// <summary>A host the MSBuild packages support.</summary>
public enum MSBuildHost
{
    /// <summary>MSBuild.exe of Visual Studio 2026, on .NET Framework.</summary>
    MSBuildExe,

    /// <summary><c>dotnet msbuild</c> of the pinned .NET 10 SDK.</summary>
    Dotnet,
}

/// <summary>What one build printed and how it ended.</summary>
/// <param name="ExitCode">The host's exit code.</param>
/// <param name="Log">The normal-verbosity file log, read as UTF-8.</param>
/// <param name="Elapsed">How long the host ran.</param>
internal sealed record MSBuildRun(int ExitCode, string Log, TimeSpan Elapsed);

/// <summary>
/// A throwaway folder to build projects in with a real MSBuild host: a copy of the built task
/// assemblies, the repository's global.json (so dotnet runs the pinned SDK, not whatever is
/// newest), and projects written by the test. One node, no node reuse: the host exits with the
/// build and no assembly stays locked.
/// </summary>
internal sealed class MSBuildSandbox : IDisposable
{
    // Variables a test runner sets for its own MSBuild; a host started from it must not inherit them.
    private static readonly string[] InheritedMSBuildVariables =
        ["MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildLoadMicrosoftTargetsReadOnly", "VisualStudioVersion"];

    public MSBuildSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "daml-msbuild-" + Guid.NewGuid().ToString("N"));
        Tasks = Path.Combine(Root, "tasks");
        Directory.CreateDirectory(Tasks);
        foreach (var file in Directory.GetFiles(BuiltTasks, "*.dll"))
        {
            File.Copy(file, Path.Combine(Tasks, Path.GetFileName(file)));
        }
        File.Copy(Path.Combine(Repository.Root, "global.json"), Path.Combine(Root, "global.json"));
    }

    /// <summary>The sandbox folder.</summary>
    public string Root { get; }

    /// <summary>The folder the tasks load from, as a package's would.</summary>
    public string Tasks { get; }

    /// <summary>The task assembly projects point their <c>UsingTask</c> at.</summary>
    public string TaskAssembly => Path.Combine(Tasks, "Daml.Build.Tasks.dll");

    // The tasks built in the same configuration as these tests.
    private static string BuiltTasks
    {
        get
        {
            var configuration = typeof(MSBuildSandbox).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
            var folder = Path.Combine(Repository.Root, "src", "Daml.Build.Tasks", "bin", configuration, "netstandard2.0");
            return File.Exists(Path.Combine(folder, "Daml.Build.Tasks.dll"))
                ? folder
                : throw new InvalidOperationException($"Daml.Build.Tasks isn't built at '{folder}'. Build the solution first.");
        }
    }

    /// <summary>Writes <paramref name="content"/> to a file under the sandbox, creating its folders.</summary>
    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>Builds <paramref name="project"/> with <paramref name="host"/> at <paramref name="hostPath"/>.</summary>
    /// <param name="host">Which host.</param>
    /// <param name="hostPath">MSBuild.exe or dotnet.exe.</param>
    /// <param name="project">The project, relative to the sandbox.</param>
    /// <param name="environment">Extra variables for the host and the tools it starts.</param>
    /// <param name="properties">Global properties; <c>DamlTasksAssembly</c> is always set.</param>
    public MSBuildRun Build(
        MSBuildHost host,
        string hostPath,
        string project,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        var log = Path.Combine(Root, $"build-{Guid.NewGuid():N}.log");
        var arguments = new List<string>();
        if (host == MSBuildHost.Dotnet)
        {
            arguments.Add("msbuild");
        }
        arguments.AddRange([Path.Combine(Root, project), "-nologo", "-nodeReuse:false", "-maxCpuCount:1", "-verbosity:minimal",
            $"-fileLoggerParameters:LogFile={log};Verbosity=normal;Encoding=UTF-8", $"-property:DamlTasksAssembly={TaskAssembly}"]);
        foreach (var property in properties ?? new Dictionary<string, string>())
        {
            arguments.Add($"-property:{property.Key}={property.Value}");
        }

        var startInfo = new ProcessStartInfo(hostPath)
        {
            Arguments = string.Join(" ", arguments.Select(Quote)),
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var name in InheritedMSBuildVariables)
        {
            startInfo.EnvironmentVariables.Remove(name);
        }
        startInfo.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var variable in environment ?? new Dictionary<string, string>())
        {
            startInfo.EnvironmentVariables[variable.Key] = variable.Value;
        }

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        // Drained but unused: the file log is the record, and in UTF-8.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(300_000))
        {
            process.Kill();
            throw new TimeoutException($"{host} didn't finish {project} within 5 minutes.");
        }
        process.WaitForExit();
        return new MSBuildRun(process.ExitCode, File.Exists(log) ? File.ReadAllText(log, Encoding.UTF8) : "", clock.Elapsed);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A host still holding a file; the temp folder is cleaned eventually.
        }
    }

    private static string Quote(string argument) =>
        argument.IndexOfAny([' ', '"', ';']) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
}
