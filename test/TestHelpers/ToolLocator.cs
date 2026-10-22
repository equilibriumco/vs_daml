using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Daml.TestHelpers;

/// <summary>Finds external tools the same way the build scripts do.</summary>
public static class ToolLocator
{
    private static readonly Lazy<string?> VisualStudioMSBuild = new(FindVisualStudioMSBuild);

    /// <summary>Full path of <paramref name="tool"/>, or <c>null</c> when it isn't installed.</summary>
    public static string? Find(ExternalTool tool) =>
        tool == ExternalTool.MSBuild ? VisualStudioMSBuild.Value : Find(tool, Environment.GetEnvironmentVariable, File.Exists);

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
        // dpm is a .cmd shim on Windows; java and dotnet are real exes.
        var fileNames = tool switch
        {
            ExternalTool.Dpm => new[] { "dpm.exe", "dpm.cmd" },
            ExternalTool.Dotnet => new[] { "dotnet.exe" },
            _ => new[] { "java.exe" },
        };

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
                homeDirs.Add(Path.Combine(appData, ".dpm"));
                homeDirs.Add(Path.Combine(appData, "dpm"));
            }
        }

        foreach (var home in homeDirs)
        {
            foreach (var fileName in fileNames)
            {
                yield return Path.Combine(home, "bin", fileName);
            }
        }

        // The installer's folder, for a machine whose PATH lacks it.
        if (tool == ExternalTool.Dotnet && env("ProgramFiles") is { Length: > 0 } programFiles)
        {
            yield return Path.Combine(programFiles, "dotnet", "dotnet.exe");
        }
    }

    // Match the build scripts: an MSBuild-only installation cannot resolve .NET SDK projects.
    // DAML_TEST_MSBUILD names a host directly.
    private static string? FindVisualStudioMSBuild()
    {
        if (Environment.GetEnvironmentVariable("DAML_TEST_MSBUILD") is { Length: > 0 } chosen)
        {
            return File.Exists(chosen) ? chosen : null;
        }
        var vswhere = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }
        var startInfo = new ProcessStartInfo(vswhere, @"-all -prerelease -products * -version [18.0,19.0) -requires Microsoft.Component.MSBuild -find MSBuild\Current\Bin\MSBuild.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return SelectVisualStudioMSBuild(output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()), File.Exists);
    }

    // Among those that can build .NET SDK projects, one that can also run Visual Studio's
    // design-time builds (only an IDE installs their targets) comes first; vswhere's order isn't.
    internal static string? SelectVisualStudioMSBuild(IEnumerable<string> candidates, Func<string, bool> exists)
    {
        var usable = candidates.Where(path => exists(path)
            && exists(Path.Combine(Path.GetDirectoryName(path)!, @"SdkResolvers\Microsoft.DotNet.MSBuildSdkResolver\Microsoft.DotNet.MSBuildSdkResolver.dll"))).ToList();
        return usable.FirstOrDefault(path => exists(Path.Combine(MSBuildRoot(path), @"Microsoft\VisualStudio\Managed\Microsoft.Managed.DesignTime.targets")))
            ?? usable.FirstOrDefault();
    }

    // <root>\MSBuild from <root>\MSBuild\Current\Bin\MSBuild.exe.
    private static string MSBuildRoot(string msbuildExe) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(msbuildExe)!)!)!;
}
