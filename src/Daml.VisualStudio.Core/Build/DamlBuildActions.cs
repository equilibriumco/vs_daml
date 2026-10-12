using System;
using System.Collections.Generic;
using System.IO;
using Daml.Build.Core;
using Daml.VisualStudio.Core.Project;

namespace Daml.VisualStudio.Core.Build;

/// <summary>A dpm command offered for a manifest, and where it runs.</summary>
/// <param name="Kind">The command.</param>
/// <param name="WorkingDirectory">The manifest's folder.</param>
/// <param name="AllPackages">Run on every package of the folder's multi-package.yaml.</param>
public sealed record DamlBuildAction(DpmCommandKind Kind, string WorkingDirectory, bool AllPackages)
{
    /// <summary>Name for menus and logs: "Build", "Test", "Clean", or "Build All" and "Clean All".</summary>
    public string DisplayName => Kind + (AllPackages ? " All" : "");

    /// <summary>The manifest the command was offered for: multi-package.yaml for the "All" commands, else daml.yaml.</summary>
    public string ManifestPath =>
        Path.Combine(WorkingDirectory, AllPackages ? WorkspaceLocator.MultiPackageManifestName : WorkspaceLocator.PackageManifestName);
}

/// <summary>Which dpm commands a Daml manifest offers.</summary>
public static class DamlBuildActions
{
    /// <summary>
    /// Build, Test and Clean for a package's daml.yaml; Build All and Clean All for a
    /// multi-package.yaml (dpm tests one package at a time). Nothing for any other file.
    /// </summary>
    public static IReadOnlyList<DamlBuildAction> For(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return [];
        }
        var name = Path.GetFileName(filePath);
        var folder = Path.GetDirectoryName(filePath) ?? "";
        if (name.Equals(WorkspaceLocator.PackageManifestName, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new DamlBuildAction(DpmCommandKind.Build, folder, false),
                new DamlBuildAction(DpmCommandKind.Test, folder, false),
                new DamlBuildAction(DpmCommandKind.Clean, folder, false),
            ];
        }
        if (name.Equals(WorkspaceLocator.MultiPackageManifestName, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new DamlBuildAction(DpmCommandKind.Build, folder, true),
                new DamlBuildAction(DpmCommandKind.Clean, folder, true),
            ];
        }
        return [];
    }
}
