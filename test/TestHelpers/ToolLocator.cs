using System;
using System.Collections.Generic;
using System.IO;

namespace Daml.TestHelpers;

/// <summary>Finds external tools the same way the build scripts do.</summary>
public static class ToolLocator
{
    /// <summary>Full path of <paramref name="tool"/>, or <c>null</c> when it isn't installed.</summary>
    public static string? Find(ExternalTool tool) => Find(tool, Environment.GetEnvironmentVariable, File.Exists);

    internal static string? Find(ExternalTool tool, Func<string, string?> getEnvironmentVariable, Func<string, bool> fileExists)
    {
        foreach (var candidate in Candidates(tool, getEnvironmentVariable))
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(ExternalTool tool, Func<string, string?> env)
    {
        // dpm is a .cmd shim on Windows; java is a real exe.
        var fileNames = tool == ExternalTool.Dpm ? new[] { "dpm.exe", "dpm.cmd" } : new[] { "java.exe" };

        // The codegen puts JAVA_HOME\bin ahead of PATH, so that java is the one that runs.
        if (tool == ExternalTool.Java && env("JAVA_HOME") is { Length: > 0 } javaHome)
        {
            yield return Path.Combine(javaHome, "bin", "java.exe");
        }

        foreach (var dir in (env("PATH") ?? "").Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var fileName in fileNames)
            {
                yield return Path.Combine(dir.Trim(), fileName);
            }
        }

        // A fresh dpm install doesn't put itself on PATH; DPM_HOME wins over the default home.
        var homeDirs = new List<string>();
        if (tool == ExternalTool.Dpm)
        {
            if (env("DPM_HOME") is { Length: > 0 } dpmHome)
            {
                homeDirs.Add(dpmHome);
            }
            else if (env("APPDATA") is { Length: > 0 } appData)
            {
                homeDirs.Add(Path.Combine(appData, "dpm"));
                homeDirs.Add(Path.Combine(appData, ".dpm"));
            }
        }

        foreach (var home in homeDirs)
        {
            foreach (var fileName in fileNames)
            {
                yield return Path.Combine(home, "bin", fileName);
            }
        }
    }
}
