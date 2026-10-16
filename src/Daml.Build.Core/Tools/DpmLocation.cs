using System;
using System.Collections.Generic;
using System.IO;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Tools;

/// <summary>Finds the dpm a build runs, from <c>DamlDpmPath</c> or the same search the extension does.</summary>
public static class DpmLocation
{
    /// <summary>
    /// The full path of dpm: <paramref name="configured"/> when it's a path (relative to the
    /// project), a command looked up on PATH when it's a bare name, else the first dpm on PATH
    /// and then in <c>%APPDATA%\dpm\bin</c>. A set value that leads nowhere is an error, never a
    /// fallback to some other dpm.
    /// </summary>
    /// <param name="configured"><c>DamlDpmPath</c>; null or empty to search.</param>
    /// <param name="projectDirectory">The full path of the project's folder.</param>
    /// <param name="environment">Environment variables by name.</param>
    /// <param name="files">The file system to look in.</param>
    /// <param name="errors">Gets a DAML0101 error when dpm can't be found.</param>
    /// <returns>The path, or null with an error added.</returns>
    public static string? Find(
        string? configured,
        string projectDirectory,
        Func<string, string?> environment,
        IDamlFileSystem files,
        ICollection<ConfigurationError> errors)
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
            var appData = environment("APPDATA");
            var found = ToolSearch.Find("dpm", environment("PATH"), string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, @"dpm\bin"), files);
            if (found is null)
            {
                Missing(errors, $"dpm wasn't found on PATH or in %APPDATA%\\dpm\\bin. Install dpm (Digital Asset's package manager), or set {name} to it.");
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

    private static void Missing(ICollection<ConfigurationError> errors, string message) =>
        errors.Add(new ConfigurationError(DamlDiagnosticCodes.MissingTool, message));
}
