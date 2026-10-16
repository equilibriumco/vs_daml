using System.Collections.Generic;

namespace Daml.Build.Core.Manifests;

/// <summary>
/// The fields of a package's <c>daml.yaml</c> that the extension uses. Values are as written in the
/// file (relative paths stay relative to <see cref="PackageRoot"/>); absent fields are <c>null</c>
/// or empty. Keys the extension doesn't know are ignored.
/// </summary>
public sealed record DamlPackageManifest
{
    /// <summary>Full path of the <c>daml.yaml</c>.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Folder holding the manifest: the package root.</summary>
    public required string PackageRoot { get; init; }

    /// <summary><c>sdk-version</c>.</summary>
    public string? SdkVersion { get; init; }

    /// <summary><c>name</c>.</summary>
    public string? Name { get; init; }

    /// <summary><c>version</c>.</summary>
    public string? Version { get; init; }

    /// <summary><c>source</c>: a folder or a single <c>.daml</c> file, relative to the package root.</summary>
    public string? Source { get; init; }

    /// <summary><c>components</c> (dpm): component names or OCI references.</summary>
    public IReadOnlyList<string> Components { get; init; } = [];

    /// <summary><c>dependencies</c>.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary><c>data-dependencies</c>: DAR paths, relative to the package root.</summary>
    public IReadOnlyList<string> DataDependencies { get; init; } = [];

    /// <summary><c>build-options</c>.</summary>
    public IReadOnlyList<string> BuildOptions { get; init; } = [];

    /// <summary><c>module-prefixes</c>: package (<c>name-version</c>) to module prefix.</summary>
    public IReadOnlyDictionary<string, string> ModulePrefixes { get; init; } = new Dictionary<string, string>();
}
