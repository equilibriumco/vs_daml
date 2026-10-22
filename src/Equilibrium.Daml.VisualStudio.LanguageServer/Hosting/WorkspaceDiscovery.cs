using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.VisualStudio.Core.Project;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;

/// <summary>
/// Turns what VS has open (a folder or solution directory, the active file) into the context
/// the server runs in. Without any daml.yaml or multi-package.yaml to go by, there is nothing
/// for the server to do and no context is returned.
/// </summary>
public sealed class WorkspaceDiscovery
{
    private readonly IDamlFileSystem _fileSystem;
    private readonly WorkspaceLocator _locator;

    /// <summary>Creates the discovery over the given file system.</summary>
    public WorkspaceDiscovery(IDamlFileSystem fileSystem, IManifestReader manifests)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _locator = new WorkspaceLocator(fileSystem, manifests ?? throw new ArgumentNullException(nameof(manifests)));
    }

    /// <summary>
    /// The context for <paramref name="workspaceRoot"/> and <paramref name="activeFile"/>, either of
    /// which may be unknown, for the kind of server in <paramref name="mode"/>.
    /// </summary>
    /// <param name="workspaceRoot">The opened folder or the solution's folder.</param>
    /// <param name="activeFile">The active Daml file.</param>
    /// <param name="version">The context's version.</param>
    /// <param name="mode">How the server is started.</param>
    /// <param name="solutionManifests">The daml.yaml of each Daml project of the solution; none for an open folder.</param>
    public WorkspaceContext? Discover(string? workspaceRoot, string? activeFile, int version, ServerMode mode = ServerMode.MultiIde, IReadOnlyCollection<string>? solutionManifests = null)
    {
        var manifests = (solutionManifests ?? []).Where(_fileSystem.FileExists).Select(DamlPath.Normalize).Distinct(DamlPath.Comparer).ToList();
        // With no Daml file active, start from a manifest at the root, as if it had been opened,
        // or else from a project of the solution, one in its folder if there is one.
        var startFrom = activeFile;
        if (startFrom is null && workspaceRoot is not null)
        {
            foreach (var name in new[] { WorkspaceLocator.MultiPackageManifestName, WorkspaceLocator.PackageManifestName })
            {
                var candidate = Path.Combine(workspaceRoot, name);
                if (_fileSystem.FileExists(candidate))
                {
                    startFrom = candidate;
                    break;
                }
            }
            startFrom ??= manifests.FirstOrDefault(m => DamlPath.IsSameOrUnder(m, workspaceRoot)) ?? manifests.FirstOrDefault();
        }
        if (startFrom is null)
        {
            return null;
        }

        var location = _locator.FindForFile(startFrom);
        // Starting from a multi-package.yaml itself (no Daml file active yet) finds no package
        // above it, but that manifest is the workspace.
        var startsAtMultiPackage = Path.GetFileName(startFrom).Equals(WorkspaceLocator.MultiPackageManifestName, StringComparison.OrdinalIgnoreCase);
        if (location.Package is null && location.MultiPackage is null && !startsAtMultiPackage)
        {
            return null;
        }
        var root = workspaceRoot
            ?? (location.MultiPackage is { } multiPackage ? Path.GetDirectoryName(multiPackage.ManifestPath)!
                : location.Package?.PackageRoot ?? Path.GetDirectoryName(startFrom)!);
        return _locator.Resolve(root, startFrom, mode, version, PackagesOf(manifests)).Context;
    }

    /// <summary>
    /// The packages the solution's Daml projects stand for: the folders of the manifests that
    /// exist, each once. A project whose daml.yaml isn't there yet has no package.
    /// </summary>
    public IReadOnlyList<string> PackagesOf(IEnumerable<string> manifests) =>
        (manifests ?? throw new ArgumentNullException(nameof(manifests)))
            .Where(_fileSystem.FileExists)
            .Select(m => Path.GetDirectoryName(DamlPath.Normalize(m))!)
            .Distinct(DamlPath.Comparer)
            .ToList();
}
