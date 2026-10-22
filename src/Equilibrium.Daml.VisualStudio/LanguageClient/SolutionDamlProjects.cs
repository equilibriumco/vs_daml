using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Microsoft.VisualStudio.Threading;

namespace Equilibrium.Daml.VisualStudio.LanguageClient;

/// <summary>
/// The Daml projects of the open solution and their manifests: projects whose evaluated
/// <c>DamlManifestPath</c> is set, as Daml.Sdk sets it to the daml.yaml next to the project. C#
/// projects and anything else have none. Read on the UI thread through each project's build
/// property storage; an open folder has no projects and gives none.
/// </summary>
internal sealed class SolutionDamlProjects
{
    private const string ManifestProperty = "DamlManifestPath";

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;

    public SolutionDamlProjects(JoinableTaskFactory joinableTaskFactory, IDamlLogger logger)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _logger = logger;
    }

    /// <summary>The daml.yaml of each loaded Daml project, in the solution's order.</summary>
    public async Task<IReadOnlyList<string>> ManifestsAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        var manifests = new List<string>();
        foreach (var project in await VS.Solutions.GetAllProjectsAsync())
        {
            if (string.IsNullOrEmpty(project.FullPath))
            {
                continue;
            }
            string? value;
            try
            {
                value = await project.GetAttributeAsync(ManifestProperty);
            }
            catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidOperationException)
            {
                continue; // a project type without MSBuild properties
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            // Daml.Sdk's default is a full path; one the project sets itself may be relative to it.
            manifests.Add(Path.IsPathRooted(value) ? value! : Path.Combine(Path.GetDirectoryName(project.FullPath)!, value));
        }
        if (manifests.Count > 0)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"Daml projects in the solution: {string.Join(", ", manifests)}.");
        }
        return manifests;
    }
}
