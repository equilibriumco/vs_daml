using System;
using System.IO;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Manifests;
using Daml.VisualStudio.Core.Project;

namespace Daml.VisualStudio.LanguageServer.Hosting;

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
    public WorkspaceContext? Discover(string? workspaceRoot, string? activeFile, int version, ServerMode mode = ServerMode.MultiIde)
    {
        // With no Daml file active, start from a manifest at the root, as if it had been opened.
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
        return _locator.Resolve(root, startFrom, mode, version).Context;
    }
}
