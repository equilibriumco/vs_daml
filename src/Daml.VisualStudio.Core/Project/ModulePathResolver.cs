using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Manifests;

namespace Daml.VisualStudio.Core.Project;

/// <summary>
/// Maps a module name, as written in a package, to its source file in the workspace. Only places
/// the package can actually import from are searched: the package itself, packages its
/// <c>module-prefixes</c> name, and workspace packages it depends on through a DAR built from them.
/// Every candidate file's header is checked, so the answer is never just "the first file with
/// that name somewhere".
/// </summary>
public sealed class ModulePathResolver
{
    private readonly IDamlFileSystem _fileSystem;

    /// <summary>Creates a resolver reading files through <paramref name="fileSystem"/>.</summary>
    public ModulePathResolver(IDamlFileSystem fileSystem) =>
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>Resolves <paramref name="moduleName"/> as imported by <paramref name="importingPackage"/>.</summary>
    /// <param name="moduleName">Module name as written in the import (may carry a module prefix).</param>
    /// <param name="importingPackage">Package whose source does the import.</param>
    /// <param name="workspacePackages">Packages of the workspace; others are treated as unknown.</param>
    public ModuleResolution Resolve(string moduleName, DamlPackageManifest importingPackage, IReadOnlyCollection<DamlPackageManifest> workspacePackages)
    {
        var reasons = new List<string>();
        var checkedFiles = new List<ModuleCandidate>();
        foreach (var (package, module) in Places(moduleName, importingPackage, workspacePackages, reasons))
        {
            if (Check(package, module, reasons) is { } candidate)
            {
                checkedFiles.Add(candidate);
            }
        }

        var matches = checkedFiles.Where(c => c.DeclaredModule == c.ModuleName).ToList();
        if (matches.Count == 1)
        {
            var match = matches[0];
            return new ModuleResolution
            {
                Status = ModuleResolutionStatus.Found,
                Path = match.Path,
                PackageRoot = match.PackageRoot,
                ModuleName = match.ModuleName,
                Candidates = matches,
            };
        }
        if (matches.Count > 1)
        {
            return new ModuleResolution { Status = ModuleResolutionStatus.Ambiguous, Candidates = matches };
        }
        if (checkedFiles.Count > 0)
        {
            return new ModuleResolution { Status = ModuleResolutionStatus.Stale, Candidates = checkedFiles };
        }

        reasons.Add($"No file declares module {moduleName} in {importingPackage.Name ?? importingPackage.PackageRoot} or the workspace packages it depends on.");
        return new ModuleResolution { Status = ModuleResolutionStatus.Missing, Reason = string.Join(" ", reasons) };
    }

    // Every (package, module-inside-it) pair the import could refer to.
    private static IEnumerable<(DamlPackageManifest Package, string Module)> Places(
        string moduleName, DamlPackageManifest importing, IReadOnlyCollection<DamlPackageManifest> workspace, List<string> reasons)
    {
        yield return (importing, moduleName);

        // module-prefixes: "lib-1.0.0: V1" makes lib's module Asset importable as V1.Asset, and only so.
        foreach (var prefix in importing.ModulePrefixes)
        {
            if (!moduleName.StartsWith(prefix.Value + ".", StringComparison.Ordinal))
            {
                continue;
            }
            if (FindByKey(workspace, prefix.Key) is { } prefixed)
            {
                yield return (prefixed, moduleName.Substring(prefix.Value.Length + 1));
            }
            else
            {
                reasons.Add($"Prefix '{prefix.Value}' refers to package {prefix.Key}, which is not in the workspace.");
            }
        }

        // Data dependencies on DARs that dpm builds as <name>-<version>.dar from a workspace package.
        foreach (var dar in importing.DataDependencies.Where(DamlDependency.IsLocalDar))
        {
            var key = Path.GetFileNameWithoutExtension(dar);
            if (importing.ModulePrefixes.ContainsKey(key))
            {
                continue;
            }
            if (FindByKey(workspace, key) is { } dependency && !ReferenceEquals(dependency, importing))
            {
                yield return (dependency, moduleName);
            }
        }
    }

    private static DamlPackageManifest? FindByKey(IReadOnlyCollection<DamlPackageManifest> workspace, string key) =>
        workspace.FirstOrDefault(p => p.Name is not null && p.Version is not null && $"{p.Name}-{p.Version}" == key);

    private ModuleCandidate? Check(DamlPackageManifest package, string module, List<string> reasons)
    {
        if (package.Source is not { Length: > 0 } source)
        {
            reasons.Add($"Package {package.Name ?? package.PackageRoot} has no 'source' in its daml.yaml.");
            return null;
        }

        // 'source' is either the folder modules live under or one .daml file whose folder is that root.
        string sourcePath;
        try
        {
            sourcePath = DamlPath.Resolve(package.PackageRoot, source);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reasons.Add($"Package {package.Name ?? package.PackageRoot} has a 'source' that isn't a valid path: {source}");
            return null;
        }
        var moduleRoot = sourcePath.EndsWith(".daml", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(sourcePath)! : sourcePath;
        var path = Path.Combine(moduleRoot, module.Replace('.', '\\') + ".daml");
        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = _fileSystem.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reasons.Add($"Can't read {path}: {ex.Message}");
            return null;
        }
        return new ModuleCandidate(package.PackageRoot, module, path, ModuleHeader.DeclaredModule(text));
    }
}
