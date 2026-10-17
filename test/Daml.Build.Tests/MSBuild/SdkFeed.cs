using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Daml.TestHelpers;

namespace Daml.Build.Tests.MSBuild;

// A package folder for sandboxes to resolve the Daml packages from, as users do: Daml.Sdk and
// Daml.Build.CSharp packed once per test run, under versions of their own so no NuGet cache can
// hold an older copy, next to the NoTargets package the SDK imports and the runtime the generated
// C# compiles against, both taken from this machine's NuGet cache. No network is involved.
internal static class SdkFeed
{
    private const string NoTargets = "microsoft.build.notargets";
    private const string NoTargetsVersion = "3.7.134";

    /// <summary>The runtime the generated C# compiles against, in the version the generator pins.</summary>
    public const string RuntimeVersion = "0.6.0";

    private static readonly (string Id, string Version)[] FromCache =
        [(NoTargets, NoTargetsVersion), ("daml.runtime", RuntimeVersion), ("daml.ledger.abstractions", RuntimeVersion)];

    private static readonly Lazy<string> Created = new(Create);

    /// <summary>The version the packages are packed under.</summary>
    public static string Version { get; } = $"0.0.0-test.{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    /// <summary>A later patch of the same packages, as a solution with projects a release apart has it.</summary>
    public static string NextPatch { get; } = $"0.0.1-test.{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    /// <summary>The folder holding the packages.</summary>
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
        foreach (var project in new[] { @"src\Daml.Sdk\Daml.Sdk.csproj", @"src\Daml.Build.CSharp\Daml.Build.CSharp.csproj" })
        {
            Pack(folder, project, Version);
            Pack(folder, project, NextPatch);
        }

        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var (id, version) in FromCache)
        {
            var package = Path.Combine(packages, id, version, $"{id}.{version}.nupkg");
            if (!File.Exists(package))
            {
                throw new InvalidOperationException($"{package} isn't in the NuGet cache; restoring the solution puts it there.");
            }
            File.Copy(package, Path.Combine(folder, Path.GetFileName(package)));
        }
        return folder;
    }

    private static void Pack(string folder, string relativeProject, string version)
    {
        var project = Path.Combine(Repository.Root, relativeProject);
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
            throw new InvalidOperationException($"Packing {Path.GetFileNameWithoutExtension(project)} {version} failed ({pack.ExitCode}): {output}");
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
