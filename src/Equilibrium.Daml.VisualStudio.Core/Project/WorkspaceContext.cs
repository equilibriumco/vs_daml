using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;

namespace Equilibrium.Daml.VisualStudio.Core.Project;

/// <summary>How the language server is started.</summary>
public enum ServerMode
{
    /// <summary><c>damlc multi-ide</c>: one coordinator serving every package it is asked about.</summary>
    MultiIde,

    /// <summary><c>damlc ide</c>: a single package; needs that package's <c>daml.yaml</c>.</summary>
    Ide,
}

/// <summary>Why no workspace context could be built.</summary>
public enum WorkspaceProblem
{
    /// <summary>No problem.</summary>
    None,

    /// <summary>Single-package mode was asked for, but the file has no <c>daml.yaml</c> above it.</summary>
    NoPackageManifest,
}

/// <summary>
/// One language server session's view of the workspace. The roots are kept apart on purpose:
/// <see cref="WorkspaceRoot"/> is what the user opened, <see cref="PackageRoot"/> is where the
/// manifest of the file that started the session lives, and <see cref="ServerWorkingDirectory"/>
/// is where the server process runs (which decides how damlc finds <c>multi-package.yaml</c>).
/// </summary>
public sealed record WorkspaceContext
{
    /// <summary>The opened folder, or the solution's folder.</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Working directory for the server process.</summary>
    public required string ServerWorkingDirectory { get; init; }

    /// <summary>How the server is started.</summary>
    public required ServerMode Mode { get; init; }

    /// <summary>Package of the file the session was resolved for; <c>null</c> if it has no <c>daml.yaml</c>.</summary>
    public string? PackageRoot { get; init; }

    /// <summary>The <c>multi-package.yaml</c> in <see cref="ServerWorkingDirectory"/>, if there is one.</summary>
    public string? MultiPackageManifestPath { get; init; }

    /// <summary>Package folders known to belong to the workspace, including listed ones outside the root.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>
    /// The packages of the solution's Daml projects the context was made for, wherever they are;
    /// empty for an open folder.
    /// </summary>
    public IReadOnlyList<string> SolutionPackages { get; init; } = [];

    /// <summary>Increases with every new context; results from an older one are stale.</summary>
    public required int Version { get; init; }

    /// <summary>Problems in the manifests the context was built from.</summary>
    public IReadOnlyList<ManifestDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// True when <paramref name="path"/> is under the workspace root or one of its packages. Files
    /// elsewhere get local syntax features only and never switch the running server.
    /// </summary>
    public bool Contains(string path) =>
        DamlPath.IsSameOrUnder(path, WorkspaceRoot) || Packages.Any(p => DamlPath.IsSameOrUnder(path, p));

    /// <summary>
    /// True when the context was made for exactly these solution packages; a project added to or
    /// removed from the solution makes a new one due.
    /// </summary>
    public bool IsFor(IEnumerable<string> solutionPackages)
    {
        var current = new HashSet<string>(SolutionPackages, DamlPath.Comparer);
        return current.SetEquals(solutionPackages.Select(DamlPath.Normalize));
    }

    /// <summary>
    /// True when a change to <paramref name="path"/> (created, edited or deleted) can change which
    /// packages exist or where the server should run, so this context and its session are stale.
    /// </summary>
    public bool IsInvalidatedBy(string path)
    {
        var name = Path.GetFileName(path);
        var isManifest = name.Equals(WorkspaceLocator.PackageManifestName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(WorkspaceLocator.MultiPackageManifestName, StringComparison.OrdinalIgnoreCase);
        return isManifest && Contains(path);
    }
}

/// <summary>Outcome of <see cref="WorkspaceLocator.Resolve"/>: a context, or the reason there is none.</summary>
/// <param name="Context">The context, or <c>null</c> when <paramref name="Problem"/> says why not.</param>
/// <param name="Problem">Why no context could be built.</param>
public sealed record WorkspaceResolution(WorkspaceContext? Context, WorkspaceProblem Problem);
