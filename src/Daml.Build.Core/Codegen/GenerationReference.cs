namespace Daml.Build.Core.Codegen;

/// <summary>What a generation reads.</summary>
public enum DamlReferenceKind
{
    /// <summary>A Daml project; its DAR comes from building it.</summary>
    Project,

    /// <summary>A DAR file used as it is.</summary>
    Dar,
}

/// <summary>A reference the project generates bindings from, with the identity its output is kept under.</summary>
/// <param name="Source">The item that declared it.</param>
/// <param name="Include">The path as the project wrote it, for messages.</param>
/// <param name="Path">The canonical full path of the project file or DAR.</param>
/// <param name="Namespace">The C# namespace prefix, or null for the generator's default.</param>
/// <param name="GenerationId">The identity of this reference's generated code.</param>
/// <param name="FolderName">The folder, under the generation root, that holds the generated code.</param>
public sealed record GenerationReference(
    DamlReferenceSource Source,
    string Include,
    string Path,
    string? Namespace,
    string GenerationId,
    string FolderName)
{
    /// <summary>Whether the reference is a project or a DAR.</summary>
    public DamlReferenceKind Kind => Source == DamlReferenceSource.DamlDar ? DamlReferenceKind.Dar : DamlReferenceKind.Project;
}
