using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class BuildTests
{
    // Building calls the real dpm, which can be slow on a cold SDK.
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(4);

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Building_the_broken_sample_puts_daml_build_errors_in_the_error_list()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        // A copy, so dpm's .daml output doesn't land in the repository.
        var folder = CopySample("broken");

        var solution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution7 ?? throw new InvalidOperationException("SVsSolution is not available.");
        solution.OpenFolder(folder);
        var dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE ?? throw new InvalidOperationException("DTE is not available.");
        // In Open Folder, building everything runs every build context, ours for daml.yaml
        // included. It only becomes available once the folder's build index is ready, and which
        // command carries it isn't the same as with a solution, so take the first that's offered.
        string[] candidates = ["Build.BuildAll", "Build.BuildSolution", "Build.BuildSelection"];
        string? available = null;
        var ready = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (available is null && DateTime.UtcNow < ready)
        {
            available = candidates.FirstOrDefault(name => IsAvailable(dte, name));
            if (available is null)
            {
                await Task.Delay(500);
            }
        }
        Assert.True(available is not null, $"No build command became available for {folder} (open: {dte.Solution?.FullName}): {string.Join(", ", candidates)}");
        dte.ExecuteCommand(available);

        var errorList = ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList)) as IErrorList ?? throw new InvalidOperationException("The Error List is not available.");
        var deadline = DateTime.UtcNow + BuildTimeout;
        var count = 0;
        while (DateTime.UtcNow < deadline)
        {
            await errorList.TableControl.ForceUpdateAsync();
            // Build errors reach the Error List through Visual Studio's build messages, under our codes.
            count = errorList.TableControl.Entries.Count(e => e.TryGetValue(StandardTableKeyNames.ErrorCode, out var code) && Equals(code, "DAML0001"));
            if (count > 0)
            {
                break;
            }
            await Task.Delay(1000);
        }

        Assert.True(count >= 1, "No DAML0001 build errors reached the Error List.");
    }

    private static bool IsAvailable(DTE dte, string command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return dte.Commands.Item(command).IsAvailable;
        }
        catch (ArgumentException)
        {
            // Not a command this Visual Studio knows.
            return false;
        }
    }

    private static string CopySample(string name)
    {
        var source = Path.Combine(RepoRoot(), "samples", name);
        var target = Path.Combine(Path.GetTempPath(), "daml-ide-build-" + Guid.NewGuid().ToString("N"), name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            if (relative.StartsWith(".daml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }
        return target;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(BuildTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daml.VisualStudio.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found above the test assembly.");
    }
}
