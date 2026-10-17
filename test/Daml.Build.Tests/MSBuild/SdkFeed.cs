using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Daml.TestHelpers;

namespace Daml.Build.Tests.MSBuild;

// A package folder for sandboxes to resolve Daml.Sdk from, as users do: the SDK packed once per
// test run, under a version of its own so no NuGet cache can hold an older copy, next to the
// NoTargets package it imports, taken from this machine's NuGet cache. No network is involved.
internal static class SdkFeed
{
    private const string NoTargets = "microsoft.build.notargets";
    private const string NoTargetsVersion = "3.7.134";

    private static readonly Lazy<string> Created = new(Create);

    /// <summary>The version the SDK is packed under.</summary>
    public static string Version { get; } = $"0.0.0-test.{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    /// <summary>A later patch of the same SDK, as a solution with projects a release apart has it.</summary>
    public static string NextPatch { get; } = $"0.0.1-test.{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    /// <summary>The folder holding Daml.Sdk and NoTargets packages.</summary>
    public static string Folder => Created.Value;

    private static string Configuration =>
        typeof(SdkFeed).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    private static string Create()
    {
        // A test host killed at the end of a run never gets to clean up, so feeds of runs before go too.
        var feeds = Path.Combine(Path.GetTempPath(), "daml-sdk-feeds");
        if (Directory.Exists(feeds))
        {
            foreach (var old in Directory.GetDirectories(feeds).Where(f => Directory.GetCreationTimeUtc(f) < DateTime.UtcNow.AddDays(-1)))
            {
                TryDelete(old);
            }
        }
        var folder = Path.Combine(feeds, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(folder);

        // From what the solution build left, so nothing is rebuilt under tests that read it.
        Pack(folder, Version);
        Pack(folder, NextPatch);

        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var noTargets = Path.Combine(packages, NoTargets, NoTargetsVersion, $"{NoTargets}.{NoTargetsVersion}.nupkg");
        if (!File.Exists(noTargets))
        {
            throw new InvalidOperationException($"{noTargets} isn't in the NuGet cache; restoring the solution puts it there.");
        }
        File.Copy(noTargets, Path.Combine(folder, Path.GetFileName(noTargets)));
        return folder;
    }

    private static void Pack(string folder, string version)
    {
        var project = Path.Combine(Repository.Root, @"src\Daml.Sdk\Daml.Sdk.csproj");
        var startInfo = new ProcessStartInfo(RequiredTools.Require(ExternalTool.Dotnet))
        {
            Arguments = $"pack \"{project}\" --no-build -c {Configuration} -o \"{folder}\" -p:PackageVersion={version} \"-p:NuspecOutputPath={Path.Combine(folder, "nuspec")}\" -nologo",
            WorkingDirectory = Repository.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var output = new StringBuilder();
        using var pack = new Process { StartInfo = startInfo };
        pack.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
        pack.ErrorDataReceived += (_, e) => output.AppendLine(e.Data);
        pack.Start();
        pack.BeginOutputReadLine();
        pack.BeginErrorReadLine();
        pack.WaitForExit();
        if (pack.ExitCode != 0)
        {
            throw new InvalidOperationException($"Packing Daml.Sdk {version} failed ({pack.ExitCode}): {output}");
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A host may still hold a package; a later run takes it.
        }
    }
}
