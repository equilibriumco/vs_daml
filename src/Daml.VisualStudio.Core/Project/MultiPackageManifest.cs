using System.Collections.Generic;

namespace Daml.VisualStudio.Core.Project;

/// <summary>A <c>multi-package.yaml</c>, with every path resolved against its folder.</summary>
public sealed record MultiPackageManifest
{
    /// <summary>Full path of the <c>multi-package.yaml</c>.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Folder holding the manifest.</summary>
    public required string Root { get; init; }

    /// <summary><c>sdk-version</c>, if given.</summary>
    public string? SdkVersion { get; init; }

    /// <summary><c>packages</c>: full paths of the package folders.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary><c>dars</c>: full paths of DARs that belong to the workspace.</summary>
    public IReadOnlyList<string> Dars { get; init; } = [];
}
