namespace Equilibrium.Daml.Build.Core.Configuration;

/// <summary>Which projects read a property.</summary>
public enum DamlPropertyScope
{
    /// <summary>Daml projects and C# projects with generated bindings alike.</summary>
    Shared,

    /// <summary>Daml projects, built with the Daml SDK.</summary>
    DamlProject,

    /// <summary>C# projects that generate bindings from DARs.</summary>
    CSharpProject,
}

/// <summary>A property users may set in a project file.</summary>
/// <param name="Name">The MSBuild property name.</param>
/// <param name="Scope">Which projects read it.</param>
/// <param name="Default">The default as the package declares it; empty when the build works it out.</param>
/// <param name="Allowed">What values are accepted, worded to finish "it has to be …" in error messages.</param>
public sealed record DamlProperty(string Name, DamlPropertyScope Scope, string Default, string Allowed);
