using System;
using System.Collections.Generic;
using System.Linq;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.VisualStudio.Core.Project;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>A C# project generating bindings from a Daml project's sources, as the binding index knows it.</summary>
/// <param name="Project">The C# project file.</param>
/// <param name="TargetFramework">The target framework, when the project builds for several; null otherwise.</param>
/// <param name="SourceManifest">The daml.yaml of the Daml project the DAR is built from.</param>
public sealed record BindingConsumer(string Project, string? TargetFramework, string SourceManifest);

/// <summary>A C# project, in one target framework when it builds for several, whose bindings a refresh brings up to date.</summary>
/// <param name="Project">The C# project file.</param>
/// <param name="TargetFramework">The target framework; null for a project with one.</param>
public sealed record BindingRefreshTarget(string Project, string? TargetFramework);

/// <summary>
/// Which C# projects a Daml edit concerns: those generating from the edited package, and those
/// generating from a package that takes the edited one's DAR as a dependency, however many steps
/// away. Packages are told apart by their manifests and the DARs they build; a manifest that can't
/// be read links nothing.
/// </summary>
public sealed class BindingSyncPlanner
{
    private readonly IDamlFileSystem _files;
    private readonly IManifestReader _reader;
    private readonly WorkspaceLocator _locator;

    /// <summary>Creates the planner reading manifests through <paramref name="reader"/>.</summary>
    public BindingSyncPlanner(IDamlFileSystem files, IManifestReader reader)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _locator = new WorkspaceLocator(files, reader);
    }

    /// <summary>The projects whose bindings an edit of <paramref name="changedFile"/> can change, each once.</summary>
    /// <param name="changedFile">The saved Daml file or manifest.</param>
    /// <param name="consumers">What the binding index knows of the projects that generate from Daml projects.</param>
    /// <param name="packageManifests">The workspace's packages, to follow dependencies through.</param>
    public IReadOnlyList<BindingRefreshTarget> TargetsFor(string changedFile, IEnumerable<BindingConsumer> consumers, IEnumerable<string> packageManifests)
    {
        if (consumers is null)
        {
            throw new ArgumentNullException(nameof(consumers));
        }
        if (packageManifests is null)
        {
            throw new ArgumentNullException(nameof(packageManifests));
        }
        if (_locator.FindForFile(changedFile).Package?.ManifestPath is not { } changed)
        {
            return [];
        }
        var all = consumers.ToList();
        var packages = packageManifests.Concat(all.Select(c => c.SourceManifest)).Append(changed)
            .Select(DamlPath.Normalize)
            .Distinct(DamlPath.Comparer)
            .Select(m => DamlPackageBuild.Read(_reader, _files, m).Value)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        var affected = new HashSet<string>(DamlPath.Comparer) { changed };
        var dars = new HashSet<string>(packages.Where(p => affected.Contains(p.ManifestPath)).Select(p => p.DarPath), DamlPath.Comparer);
        bool grew;
        do
        {
            grew = false;
            foreach (var package in packages.Where(p => !affected.Contains(p.ManifestPath)))
            {
                if (package.Dependencies.Concat(package.DataDependencies).Any(d => d.DarPath is { } dar && dars.Contains(dar)))
                {
                    affected.Add(package.ManifestPath);
                    dars.Add(package.DarPath);
                    grew = true;
                }
            }
        }
        while (grew);

        return all.Where(c => affected.Contains(DamlPath.Normalize(c.SourceManifest)))
            .Select(c => new BindingRefreshTarget(c.Project, c.TargetFramework))
            .Distinct()
            .ToList();
    }
}
