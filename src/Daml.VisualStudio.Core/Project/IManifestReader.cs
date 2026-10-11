namespace Daml.VisualStudio.Core.Project;

/// <summary>Reads Daml manifests. Never throws for bad content: problems come back as diagnostics.</summary>
public interface IManifestReader
{
    /// <summary>Reads a package's <c>daml.yaml</c>.</summary>
    ManifestResult<DamlPackageManifest> ReadPackage(string manifestPath);

    /// <summary>Reads a <c>multi-package.yaml</c>.</summary>
    ManifestResult<MultiPackageManifest> ReadMultiPackage(string manifestPath);
}
