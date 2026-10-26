using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>How generated C# relates to the Daml sources on this machine.</summary>
public enum SourceStatus
{
    /// <summary>The sources are still what the DAR was built from.</summary>
    Current,

    /// <summary>The sources changed after the DAR was built; the C# is of the sources as they were.</summary>
    Stale,

    /// <summary>No sources here: a DAR reference, or a project whose sources are gone.</summary>
    External,
}

/// <summary>A Daml package a symbol's C# was generated from, as one place to go to.</summary>
/// <param name="ManifestPath">The package's <c>daml.yaml</c>.</param>
/// <param name="Status">Whether its sources are still those of the C#.</param>
/// <param name="Locations">The C# declarations generated from it.</param>
public sealed record SourceCandidate(string ManifestPath, SourceStatus Status, IReadOnlyList<BindingLocation> Locations);

/// <summary>
/// Compares a generation's record of the Daml sources its DAR was built from with the sources
/// as they are now, the same way the build fingerprints them. Navigation goes only to sources
/// that match; with several, the user chooses, never the first one found.
/// </summary>
public sealed class DamlSources
{
    // The disk, always: the fingerprint hashes the source files the way the build does, which no
    // stand-in file system could.
    private readonly IDamlFileSystem _files = new PhysicalFileSystem();

    /// <summary>Whether the sources <paramref name="generation"/> records are still the Daml on this machine.</summary>
    public SourceStatus StatusOf(GenerationProvenance generation)
    {
        if ((generation ?? throw new ArgumentNullException(nameof(generation))).Source is not { } source)
        {
            return SourceStatus.External;
        }
        try
        {
            if (DamlPackageBuild.Read(new YamlManifestReader(_files), _files, source.ManifestPath).Value is not { } package)
            {
                return SourceStatus.External;
            }
            var now = PackageBuildState.SourceFingerprintOf(package.Source.Path);
            return string.Equals(now, source.Fingerprint, StringComparison.OrdinalIgnoreCase) ? SourceStatus.Current : SourceStatus.Stale;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SourceStatus.External;
        }
    }

    /// <summary>
    /// The local packages <paramref name="symbol"/>'s C# was generated from, one per <c>daml.yaml</c>:
    /// current when any generation of it matches the sources now. DAR references bring none.
    /// </summary>
    public IReadOnlyList<SourceCandidate> CandidatesFor(BindingIndex index, DamlSymbolId symbol) =>
        (index ?? throw new ArgumentNullException(nameof(index))).LocationsOf(symbol)
            .Where(l => l.Generation.Source is not null)
            .GroupBy(l => l.Generation.Source!.ManifestPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SourceCandidate(
                g.Key,
                g.Select(l => l.Generation).Distinct().Any(generation => StatusOf(generation) == SourceStatus.Current) ? SourceStatus.Current : SourceStatus.Stale,
                g.ToList()))
            .OrderBy(c => c.ManifestPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
