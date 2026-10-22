using System;
using System.Collections.Generic;
using System.IO;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Tools;

/// <summary>Finds the dpm a build runs, from <c>DamlDpmPath</c> or the same search the extension does.</summary>
public static class DpmLocation
{
    /// <summary>
    /// The full path of dpm: <paramref name="configured"/> when it's a path (relative to the
    /// project), a command looked up on PATH when it's a bare name, else the first dpm on PATH
    /// and then in DPM's home directories. A set value that leads nowhere is an error, never a
    /// fallback to some other dpm.
    /// </summary>
    /// <param name="configured"><c>DamlDpmPath</c>; null or empty to search.</param>
    /// <param name="projectDirectory">The full path of the project's folder.</param>
    /// <param name="environment">Environment variables by name.</param>
    /// <param name="files">The file system to look in.</param>
    /// <param name="errors">Gets a DAML0101 error when dpm can't be found.</param>
    /// <param name="workingDirectory">The child's working directory for relative DPM_HOME; defaults to the project directory.</param>
    /// <returns>The path, or null with an error added.</returns>
    public static string? Find(
        string? configured,
        string projectDirectory,
        Func<string, string?> environment,
        IDamlFileSystem files,
        ICollection<ConfigurationError> errors,
        string? workingDirectory = null)
    {
        if (environment is null)
        {
            throw new ArgumentNullException(nameof(environment));
        }
        if (errors is null)
        {
            throw new ArgumentNullException(nameof(errors));
        }
        var name = DamlProperties.DpmPath.Name;
        // Pasted from Explorer it may come with quotes.
        var value = configured?.Trim().Trim('"') ?? "";
        if (value.Length == 0)
        {
            var found = FindDefault(workingDirectory ?? projectDirectory, environment, files);
            if (found is null)
            {
                Missing(errors, $"dpm wasn't found on PATH or in the DPM home directories (DPM_HOME, %APPDATA%\\.dpm, %APPDATA%\\dpm). Install dpm (Digital Asset's package manager), or set {name} to it.");
            }
            return found;
        }
        if (value.IndexOfAny(['\\', '/', ':']) < 0)
        {
            var onPath = ToolSearch.Find(value, environment("PATH"), null, files);
            if (onPath is null)
            {
                Missing(errors, $"{name} is '{value}', but no {value} is on PATH.");
            }
            return onPath;
        }
        var path = DamlPath.Resolve(projectDirectory, value);
        if (!files.FileExists(path))
        {
            Missing(errors, $"{name} is '{value}', but there is no file at {path}.");
            return null;
        }
        return path;
    }

    /// <summary>DPM's configuration home, resolving relative overrides against the child process's working directory.</summary>
    public static string? HomeDirectory(string workingDirectory, Func<string, string?> environment)
    {
        var home = environment("DPM_HOME");
        if (home is null)
        {
            if (environment("APPDATA") is not { } appData) { return null; }
            home = Path.Combine(appData, "dpm");
        }
        // DPM uses LookupEnv: a present empty value means the working directory.
        return Path.GetFullPath(Path.Combine(string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory, home));
    }

    private static string? FindDefault(string projectDirectory, Func<string, string?> environment, IDamlFileSystem files)
    {
        if (environment is null)
        {
            throw new ArgumentNullException(nameof(environment));
        }
        var onPath = ToolSearch.Find("dpm", environment("PATH"), null, files);
        if (onPath is not null)
        {
            return onPath;
        }
        if (environment("DPM_HOME") is not null && HomeDirectory(projectDirectory, environment) is { } home
            && ToolSearch.Find("dpm", Path.Combine(home, "bin"), null, files) is { } inHome)
        {
            return inHome;
        }
        return environment("APPDATA") is { Length: > 0 } appData
            ? ToolSearch.Find("dpm", Path.Combine(appData, @".dpm\bin"), null, files)
                ?? ToolSearch.Find("dpm", Path.Combine(appData, @"dpm\bin"), null, files)
            : null;
    }

    private static void Missing(ICollection<ConfigurationError> errors, string message) =>
        errors.Add(new ConfigurationError(DamlDiagnosticCodes.MissingTool, message));
}
