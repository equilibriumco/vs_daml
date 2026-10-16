namespace Daml.Build.Core.Codegen;

/// <summary>How a C# project names something to generate bindings from.</summary>
public enum DamlReferenceSource
{
    /// <summary>A <c>DamlReference</c> item: a Daml project in the solution.</summary>
    DamlReference,

    /// <summary>A <c>DamlDar</c> item: a DAR file from outside the solution.</summary>
    DamlDar,

    /// <summary>A plain <c>ProjectReference</c> to a Daml project, as Visual Studio adds one.</summary>
    ProjectReference,
}

/// <summary>A reference as the project file declares it.</summary>
/// <param name="Source">The item that declares it.</param>
/// <param name="Include">The path as written, relative to the project or full.</param>
/// <param name="Namespace">The C# namespace prefix for the generated code; empty or null for none.</param>
public sealed record DeclaredDamlReference(DamlReferenceSource Source, string Include, string? Namespace);
