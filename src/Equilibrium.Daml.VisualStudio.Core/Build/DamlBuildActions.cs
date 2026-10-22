using System;
using System.Collections.Generic;
using System.IO;
using Equilibrium.Daml.Build.Core;
using Equilibrium.Daml.VisualStudio.Core.Project;

namespace Equilibrium.Daml.VisualStudio.Core.Build;

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
    /// <paramref name="kind"/> for the package <paramref name="filePath"/> belongs to (the
    /// nearest daml.yaml above it), or <c>null</c> when it belongs to none.
    /// </summary>
    public static DamlBuildAction? ForPackageOf(WorkspaceLocator locator, string? filePath, DpmCommandKind kind)
    {
        if (locator is null)
        {
            throw new ArgumentNullException(nameof(locator));
        }
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }
        return locator.FindForFile(filePath!).Package?.PackageRoot is { } root
            ? new DamlBuildAction(kind, root, AllPackages: false)
            : null;
    }

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
