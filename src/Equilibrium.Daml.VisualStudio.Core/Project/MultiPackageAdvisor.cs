using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;

namespace Equilibrium.Daml.VisualStudio.Core.Project;

/// <summary>What a solution's Daml packages need for the server to navigate between them.</summary>
public enum MultiPackageAdvice
{
    /// <summary>Nothing: one package, or one multi-package.yaml lists them all.</summary>
    None,

    /// <summary>No multi-package.yaml takes the server to all of them; one in the solution folder would.</summary>
    Create,

    /// <summary>The solution folder has a multi-package.yaml, but it misses some of them.</summary>
    Incomplete,
}

/// <summary>What <see cref="MultiPackageAdvisor.Advise"/> found.</summary>
/// <param name="Kind">What is needed.</param>
/// <param name="ManifestPath">The multi-package.yaml in the solution folder: the one to create, or the one that misses packages.</param>
/// <param name="Packages">Every package for <see cref="MultiPackageAdvice.Create"/>; the missing ones for <see cref="MultiPackageAdvice.Incomplete"/>.</param>
public sealed record MultiPackageProposal(MultiPackageAdvice Kind, string ManifestPath, IReadOnlyList<string> Packages);

/// <summary>
/// The multi-package server navigates from one package to another's sources only when a
/// multi-package.yaml listing them lies in its folder or above it. With two or more Daml projects in
/// a solution this says whether such a file is missing, and what the one offered for the solution
/// folder holds. A user's existing file is only ever pointed out, never rewritten.
/// </summary>
public sealed class MultiPackageAdvisor
{
    private static readonly Regex PlainPath = new("^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant);

    private readonly IDamlFileSystem _fileSystem;
    private readonly IManifestReader _reader;
    private readonly WorkspaceLocator _locator;

    /// <summary>Creates the advisor reading manifests through <paramref name="reader"/>.</summary>
    public MultiPackageAdvisor(IDamlFileSystem fileSystem, IManifestReader reader)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _locator = new WorkspaceLocator(fileSystem, reader);
    }

    /// <summary>What the packages of <paramref name="packageManifests"/> need in <paramref name="solutionDirectory"/>.</summary>
    /// <param name="solutionDirectory">The solution's folder.</param>
    /// <param name="packageManifests">The daml.yaml of each Daml project, in the solution's order; missing ones are left out.</param>
    public MultiPackageProposal Advise(string solutionDirectory, IEnumerable<string> packageManifests)
    {
        if (packageManifests is null)
        {
            throw new ArgumentNullException(nameof(packageManifests));
        }
        var manifestPath = Path.Combine(DamlPath.Normalize(solutionDirectory), WorkspaceLocator.MultiPackageManifestName);
        var manifests = packageManifests.Select(DamlPath.Normalize).Where(_fileSystem.FileExists).Distinct(DamlPath.Comparer).ToList();
        var packages = manifests.Select(m => Path.GetDirectoryName(m)!).Distinct(DamlPath.Comparer).ToList();
        if (packages.Count < 2)
        {
            return new MultiPackageProposal(MultiPackageAdvice.None, manifestPath, []);
        }

        if (_fileSystem.FileExists(manifestPath))
        {
            var listed = _reader.ReadMultiPackage(manifestPath).Value?.Packages ?? [];
            var missing = packages.Where(p => !listed.Contains(p, DamlPath.Comparer)).ToList();
            return new MultiPackageProposal(missing.Count == 0 ? MultiPackageAdvice.None : MultiPackageAdvice.Incomplete, manifestPath, missing);
        }

        // One file above them all that lists every package already does what the offered one would.
        var listing = manifests.Select(m => _locator.FindForFile(m).MultiPackage?.ManifestPath ?? "").Distinct(DamlPath.Comparer).ToList();
        return listing is [{ Length: > 0 }]
            ? new MultiPackageProposal(MultiPackageAdvice.None, manifestPath, [])
            : new MultiPackageProposal(MultiPackageAdvice.Create, manifestPath, packages);
    }

    /// <summary>The multi-package.yaml offered for <paramref name="solutionDirectory"/>, listing <paramref name="packages"/>.</summary>
    public static string Content(string solutionDirectory, IEnumerable<string> packages)
    {
        if (packages is null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        var root = DamlPath.Normalize(solutionDirectory);
        var text = new StringBuilder()
            .Append("# The solution's Daml packages, so the Daml language server can go from one to another's sources;\n")
            .Append("# dpm build --all builds them all.\n")
            .Append("packages:\n");
        foreach (var package in packages)
        {
            text.Append("  - ").Append(Scalar(Relative(root, DamlPath.Normalize(package)))).Append('\n');
        }
        return text.ToString();
    }

    // The path from the solution folder with forward slashes, or the full path on another drive.
    private static string Relative(string root, string path)
    {
        if (!string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        var from = root.Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
        var to = path.Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < from.Length && common < to.Length && string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }
        var up = Enumerable.Repeat("..", from.Length - common);
        var steps = up.Concat(to.Skip(common)).ToList();
        return common == from.Length ? "./" + string.Join("/", steps) : string.Join("/", steps);
    }

    // Plain when nothing in it means anything to YAML; otherwise single-quoted, which keeps backslashes.
    private static string Scalar(string value) =>
        PlainPath.IsMatch(value) ? value : "'" + value.Replace("'", "''") + "'";
}
