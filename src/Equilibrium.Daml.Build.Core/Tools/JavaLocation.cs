using System;
using System.Collections.Generic;
using System.IO;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Tools;

/// <summary>
/// Finds the java the generator runs on. Its wrapper only looks on PATH, so the build puts the
/// folder of the java found here first on the PATH it starts the generator with.
/// </summary>
public static class JavaLocation
{
    /// <summary>
    /// <c>bin\java.exe</c> of <paramref name="javaHome"/> when it is set (it defaults to
    /// JAVA_HOME), else the first java.exe on PATH. A set folder without java is an error, never a
    /// fallback to another Java.
    /// </summary>
    /// <param name="javaHome"><c>DamlJavaHome</c>.</param>
    /// <param name="environment">Environment variables by name.</param>
    /// <param name="files">The file system to look in.</param>
    /// <param name="errors">Gets a DAML0102 error when there is no java to use.</param>
    /// <returns>The full path of java.exe, or null with an error added.</returns>
    public static string? Find(string? javaHome, Func<string, string?> environment, IDamlFileSystem files, ICollection<ConfigurationError> errors)
    {
        if (environment is null)
        {
            throw new ArgumentNullException(nameof(environment));
        }
        if (files is null)
        {
            throw new ArgumentNullException(nameof(files));
        }
        if (errors is null)
        {
            throw new ArgumentNullException(nameof(errors));
        }
        var name = DamlProperties.JavaHome.Name;
        var home = javaHome?.Trim().Trim('"') ?? "";
        if (home.Length > 0)
        {
            if (!Path.IsPathRooted(home))
            {
                Missing(errors, $"{name} is '{javaHome}', which isn't the full path of a JDK folder.");
                return null;
            }
            var java = Path.Combine(DamlPath.Normalize(home), "bin", "java.exe");
            if (files.FileExists(java))
            {
                return java;
            }
            Missing(errors, $"{name} is '{javaHome}', but {java} doesn't exist. Point it, or JAVA_HOME, at a JDK {ReleaseTools.MinimumJava} or later.");
            return null;
        }
        var onPath = ToolSearch.Find("java.exe", environment("PATH"), null, files);
        if (onPath is null)
        {
            Missing(errors,
                $"The C# code generator needs Java, and none was found: {name} and JAVA_HOME are empty and no java.exe is on PATH. " +
                $"Install JDK {ReleaseTools.MinimumJava} or later.");
        }
        return onPath;
    }

    private static void Missing(ICollection<ConfigurationError> errors, string message) =>
        errors.Add(new ConfigurationError(DamlDiagnosticCodes.MissingJava, message));
}
