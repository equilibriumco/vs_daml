using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.Build.Core.FileSystem;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Daml.VisualStudio.Bindings;

/// <summary>What background refreshes need to know of the open solution, read on the UI thread.</summary>
internal sealed class SolutionProjectConfigurations
{
    private readonly JoinableTaskFactory _joinableTaskFactory;

    public SolutionProjectConfigurations(JoinableTaskFactory joinableTaskFactory) => _joinableTaskFactory = joinableTaskFactory;

    /// <summary>
    /// Each loaded project with generated bindings (an <c>obj\daml</c> folder) and the configuration
    /// the solution builds it in, as Visual Studio names it (<c>Debug|AnyCPU</c>).
    /// </summary>
    public async Task<IReadOnlyList<(string Project, string Configuration)>> WithBindingsAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        var buildManager = await VS.Services.GetSolutionBuildManagerAsync();
        var found = new List<(string, string)>();
        foreach (var project in await VS.Solutions.GetAllProjectsAsync())
        {
            if (string.IsNullOrEmpty(project.FullPath) || !Directory.Exists(Path.Combine(Path.GetDirectoryName(project.FullPath)!, "obj", "daml")))
            {
                continue;
            }
            project.GetItemInfo(out var hierarchy, out _, out _);
            var configurations = new IVsProjectCfg[1];
            if (hierarchy is not null
                && ErrorHandler.Succeeded(buildManager.FindActiveProjectCfg(IntPtr.Zero, IntPtr.Zero, hierarchy, configurations))
                && configurations[0] is { } active
                && ErrorHandler.Succeeded(active.get_CanonicalName(out var name)))
            {
                found.Add((DamlPath.Normalize(project.FullPath!), name));
            }
        }
        return found;
    }

    /// <summary>Unloads and reloads <paramref name="projectFile"/>, which makes IntelliSense read it afresh.</summary>
    /// <returns>Whether the project was found and reloaded.</returns>
    public async Task<bool> ReloadAsync(string projectFile)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        var project = (await VS.Solutions.GetAllProjectsAsync()).FirstOrDefault(p => p.FullPath is { } path && DamlPath.Comparer.Equals(DamlPath.Normalize(path), projectFile));
        if (project is null || await VS.Services.GetSolutionAsync() is not IVsSolution4 solution)
        {
            return false;
        }
        project.GetItemInfo(out var hierarchy, out _, out _);
        if (hierarchy is null || ErrorHandler.Failed(((IVsSolution)solution).GetGuidOfProject(hierarchy, out var guid)))
        {
            return false;
        }
        return ErrorHandler.Succeeded(solution.UnloadProject(ref guid, (uint)_VSProjectUnloadStatus.UNLOADSTATUS_UnloadedByUser))
            && ErrorHandler.Succeeded(solution.ReloadProject(ref guid));
    }

    /// <summary>The folder of Visual Studio's own MSBuild (its <c>Common7\IDE</c>), and the solution's folder if one is open.</summary>
    public async Task<(string InstallDirectory, string? SolutionDirectory)> EnvironmentAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        var shell = await VS.Services.GetShellAsync();
        ErrorHandler.ThrowOnFailure(shell.GetProperty((int)__VSSPROPID.VSSPROPID_InstallDirectory, out var installDirectory));
        var solution = await VS.Services.GetSolutionAsync();
        var solutionDirectory = solution.GetSolutionInfo(out var directory, out _, out _) == VSConstants.S_OK && !string.IsNullOrEmpty(directory) ? directory : null;
        return ((string)installDirectory, solutionDirectory);
    }
}
