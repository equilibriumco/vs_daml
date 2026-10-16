using System.Collections.Generic;
using Daml.Build.Core.Manifests;

namespace Daml.VisualStudio.Core.Project;

/// <summary>Where a Daml file sits: its package and the multi-package manifest that lists that package.</summary>
public sealed record DamlFileLocation
{
    /// <summary>Canonical full path of the file.</summary>
    public required string FilePath { get; init; }

    /// <summary>The nearest <c>daml.yaml</c> above the file, or <c>null</c> when there is none (or it is unreadable).</summary>
    public DamlPackageManifest? Package { get; init; }

    /// <summary>The nearest <c>multi-package.yaml</c> above the package that lists it, if any.</summary>
    public MultiPackageManifest? MultiPackage { get; init; }

    /// <summary>Problems in the manifests that were read on the way.</summary>
    public IReadOnlyList<ManifestDiagnostic> Diagnostics { get; init; } = [];
}
