using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;

namespace Equilibrium.Daml.Build.Tests.MSBuild;

// The templates of this test run, installed once into a template folder of their own: dotnet new
// sees neither what the user installed nor the templates of other runs.
internal static class TemplateHive
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? s_folder;

    private static readonly Dictionary<string, string?> Quiet = new()
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
    };

    /// <summary>Runs <c>dotnet new</c> in <paramref name="folder"/>, whose global.json picks the SDK.</summary>
    /// <param name="folder">Where to run it.</param>
    /// <param name="arguments">The template's short name, its options and where it goes.</param>
    public static async Task<ProcessResult> NewAsync(string folder, params string[] arguments)
    {
        var hive = await InstallAsync();
        return await DotnetAsync(folder, ["new", .. arguments, "--debug:custom-hive", hive]);
    }

    private static async Task<string> InstallAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (s_folder is null)
            {
                var hive = Path.Combine(SdkFeed.Folder, "hive");
                // From the repository, whose global.json pins the SDK the sandboxes run.
                var install = await DotnetAsync(Repository.Root, ["new", "install", SdkFeed.Templates, "--debug:custom-hive", hive]);
                if (install.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Installing {SdkFeed.Templates} failed ({install.ExitCode}): {install.Stdout}{install.Stderr}");
                }
                s_folder = hive;
            }
            return s_folder;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Task<ProcessResult> DotnetAsync(string folder, IReadOnlyList<string> arguments) =>
        new ProcessRunner().RunAsync(
            new ProcessSpec(RequiredTools.Require(ExternalTool.Dotnet), arguments)
            {
                WorkingDirectory = folder,
                Environment = Quiet,
                Timeout = TimeSpan.FromMinutes(2),
            },
            CancellationToken.None);
}
