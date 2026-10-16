namespace Daml.Build.Core.Manifests;

/// <summary>What a package's <c>source</c> names.</summary>
public enum DamlSourceKind
{
    /// <summary>A folder of modules.</summary>
    Folder,

    /// <summary>One module; damlc follows its imports.</summary>
    File,
}

/// <summary>A package's sources as the build sees them.</summary>
/// <param name="Kind">Folder or single file.</param>
/// <param name="Path">Its full path.</param>
public sealed record DamlSource(DamlSourceKind Kind, string Path);
