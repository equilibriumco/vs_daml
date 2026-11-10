using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Messages;

/// <summary>The kinds of change in <c>workspace/didChangeWatchedFiles</c>.</summary>
public enum FileChangeType
{
    /// <summary>The file appeared.</summary>
    Created = 1,

    /// <summary>The file's content changed.</summary>
    Changed = 2,

    /// <summary>The file is gone.</summary>
    Deleted = 3,
}

/// <summary>One entry of <c>workspace/didChangeWatchedFiles</c>.</summary>
/// <param name="Uri">The file's <c>file:</c> URI.</param>
/// <param name="Type">What happened to it.</param>
public sealed record FileEvent(string Uri, FileChangeType Type);

/// <summary>The file events damlc needs to hear about that Visual Studio doesn't send, or that don't do enough on their own.</summary>
internal static class WatchedFiles
{
    /// <summary>
    /// The events for renaming <paramref name="oldPath"/> to <paramref name="newPath"/>: the old
    /// Daml source deleted and the new one created. Visual Studio reports a rename without the
    /// deletion, and until damlc hears of it, imports of the old module still resolve. Build output
    /// (damlc's own copies under <c>.daml</c>, the C# generator's work folder) is left out.
    /// </summary>
    public static IReadOnlyList<FileEvent> ForRename(string oldPath, string newPath)
    {
        if (oldPath is null)
        {
            throw new ArgumentNullException(nameof(oldPath));
        }
        if (newPath is null)
        {
            throw new ArgumentNullException(nameof(newPath));
        }
        var events = new List<FileEvent>();
        if (IsDamlSource(oldPath))
        {
            events.Add(new FileEvent(new Uri(oldPath).AbsoluteUri, FileChangeType.Deleted));
        }
        if (IsDamlSource(newPath))
        {
            events.Add(new FileEvent(new Uri(newPath).AbsoluteUri, FileChangeType.Created));
        }
        return events;
    }

    /// <summary>
    /// The events for a DAR that was just written, typically by a build: the manifest of every
    /// package in <paramref name="packages"/> that lists the DAR in <c>dependencies</c> or
    /// <c>data-dependencies</c>, reported as changed. When the DAR disappears (a clean), the
    /// multi-package server restarts those packages, which then fail without it; when the DAR
    /// comes back it leaves them failed, but a changed manifest makes it start them again.
    /// </summary>
    public static IReadOnlyList<FileEvent> ForDarWritten(string darPath, IEnumerable<DamlPackageManifest> packages)
    {
        if (darPath is null)
        {
            throw new ArgumentNullException(nameof(darPath));
        }
        if (packages is null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        var dar = DamlPath.Normalize(darPath);
        var events = new List<FileEvent>();
        foreach (var package in packages)
        {
            var uses = package.Dependencies.Concat(package.DataDependencies)
                .Where(DamlDependency.IsLocalDar)
                .Any(entry => DamlPath.Comparer.Equals(DamlPath.Resolve(package.PackageRoot, entry), dar));
            if (uses)
            {
                events.Add(new FileEvent(new Uri(package.ManifestPath).AbsoluteUri, FileChangeType.Changed));
            }
        }
        return events;
    }

    private static bool IsDamlSource(string path) =>
        Path.GetExtension(path).Equals(".daml", StringComparison.OrdinalIgnoreCase)
        && path.IndexOf(@"\.daml\", StringComparison.OrdinalIgnoreCase) < 0
        && path.IndexOf($@"\{GenerationPublisher.WorkFolderName}\", StringComparison.OrdinalIgnoreCase) < 0;
}
