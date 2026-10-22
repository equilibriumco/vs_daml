using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;

namespace Equilibrium.Daml.VisualStudio.Core.Project;

/// <summary>Finds the manifests around a Daml file and works out the language server's workspace.</summary>
public sealed class WorkspaceLocator
{
    /// <summary>File name of a package manifest.</summary>
    public const string PackageManifestName = "daml.yaml";

    /// <summary>File name of a multi-package manifest.</summary>
    public const string MultiPackageManifestName = "multi-package.yaml";

    private readonly IDamlFileSystem _fileSystem;
    private readonly IManifestReader _reader;

    /// <summary>Creates a locator reading manifests through <paramref name="reader"/>.</summary>
    public WorkspaceLocator(IDamlFileSystem fileSystem, IManifestReader reader)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>
    /// The nearest <c>daml.yaml</c> above <paramref name="filePath"/>, and the nearest
    /// <c>multi-package.yaml</c> above that package which actually lists it.
    /// </summary>
    public DamlFileLocation FindForFile(string filePath)
    {
        var file = DamlPath.Normalize(filePath);
        var diagnostics = new List<ManifestDiagnostic>();

        DamlPackageManifest? package = null;
        if (FindUpwards(Path.GetDirectoryName(file), PackageManifestName) is { } manifestPath)
        {
            var read = _reader.ReadPackage(manifestPath);
            diagnostics.AddRange(read.Diagnostics);
            package = read.Value;
        }

        // A multi-package.yaml that doesn't list this package is not its workspace; keep looking above it.
        MultiPackageManifest? multiPackage = null;
        var searchFrom = package?.PackageRoot;
        while (package is not null && multiPackage is null && FindUpwards(searchFrom, MultiPackageManifestName) is { } candidate)
        {
            var read = _reader.ReadMultiPackage(candidate);
            diagnostics.AddRange(read.Diagnostics);
            if (read.Value?.Packages.Contains(package.PackageRoot, DamlPath.Comparer) == true)
            {
                multiPackage = read.Value;
            }
            searchFrom = Path.GetDirectoryName(Path.GetDirectoryName(candidate));
        }

        return new DamlFileLocation { FilePath = file, Package = package, MultiPackage = multiPackage, Diagnostics = diagnostics };
    }

    /// <summary>
    /// Builds the session context for a server started because <paramref name="openedFile"/> was
    /// opened in <paramref name="workspaceRoot"/>. damlc ignores <c>rootUri</c> and looks for
    /// <c>multi-package.yaml</c> from its working directory upwards, so in multi-IDE mode the server
    /// runs in the nearest folder above the file that has one (if it is inside the workspace),
    /// otherwise in the workspace root. In single-package mode it runs in the package folder.
    /// </summary>
    /// <param name="workspaceRoot">The opened folder, or the solution's folder.</param>
    /// <param name="openedFile">The file the session starts for.</param>
    /// <param name="mode">How the server is started.</param>
    /// <param name="version">The context's version.</param>
    /// <param name="solutionPackages">
    /// The package folders of the solution's Daml projects, which the multi-package server serves
    /// wherever they are; none for an open folder.
    /// </param>
    public WorkspaceResolution Resolve(string workspaceRoot, string openedFile, ServerMode mode, int version, IReadOnlyCollection<string>? solutionPackages = null)
    {
        var root = DamlPath.Normalize(workspaceRoot);
        var location = FindForFile(openedFile);
        var diagnostics = new List<ManifestDiagnostic>(location.Diagnostics);
        var packageRoot = location.Package?.PackageRoot;

        if (mode == ServerMode.Ide && packageRoot is null)
        {
            return new WorkspaceResolution(null, WorkspaceProblem.NoPackageManifest);
        }

        var workingDirectory = root;
        MultiPackageManifest? multiPackage = null;
        if (mode == ServerMode.Ide)
        {
            workingDirectory = packageRoot!;
        }
        else if (FindUpwards(Path.GetDirectoryName(location.FilePath), MultiPackageManifestName) is { } nearest
            && DamlPath.IsSameOrUnder(nearest, root))
        {
            workingDirectory = Path.GetDirectoryName(nearest)!;
            if (location.MultiPackage is { } listing && DamlPath.Comparer.Equals(listing.ManifestPath, nearest))
            {
                multiPackage = listing;
            }
            else
            {
                var read = _reader.ReadMultiPackage(nearest);
                diagnostics.AddRange(read.Diagnostics.Where(d => !diagnostics.Contains(d)));
                multiPackage = read.Value;
            }
        }

        var packages = new List<string>();
        if (multiPackage is not null)
        {
            packages.AddRange(multiPackage.Packages);
        }
        if (packageRoot is not null && !packages.Contains(packageRoot, DamlPath.Comparer))
        {
            packages.Add(packageRoot);
        }
        var fromSolution = (solutionPackages ?? []).Select(DamlPath.Normalize).Distinct(DamlPath.Comparer).ToList();
        // The single-package server knows its own package only; others' files must not reach it.
        if (mode == ServerMode.MultiIde)
        {
            packages.AddRange(fromSolution.Where(p => !packages.Contains(p, DamlPath.Comparer)));
        }

        var context = new WorkspaceContext
        {
            WorkspaceRoot = root,
            ServerWorkingDirectory = workingDirectory,
            Mode = mode,
            PackageRoot = packageRoot,
            MultiPackageManifestPath = mode == ServerMode.MultiIde && multiPackage is not null ? multiPackage.ManifestPath : null,
            Packages = packages,
            SolutionPackages = fromSolution,
            Version = version,
            Diagnostics = diagnostics,
        };
        return new WorkspaceResolution(context, WorkspaceProblem.None);
    }

    // Full path of the first file called fileName in startDirectory or one of its parents.
    private string? FindUpwards(string? startDirectory, string fileName)
    {
        for (var dir = startDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, fileName);
            if (_fileSystem.FileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
