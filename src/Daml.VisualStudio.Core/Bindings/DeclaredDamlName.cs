namespace Daml.VisualStudio.Core.Bindings;

/// <summary>
/// The Daml names a generated C# declaration states. Templates and interfaces, and their choices
/// and fields, state their package and module; data types and their members state neither, which
/// leaves those to the generation the declaration belongs to.
/// </summary>
/// <param name="Kind">What the Daml declaration is.</param>
/// <param name="PackageId">The package from <c>TemplateId</c> or <c>InterfaceId</c>; null when the code doesn't state one.</param>
/// <param name="Module">The module from the same place, likewise.</param>
/// <param name="Entity">The template, interface or data type.</param>
/// <param name="Member">The choice, field or constructor, as <see cref="DamlSymbolId.Member"/> has it; null for an entity.</param>
/// <param name="Namespace">The C# namespace of the generated type.</param>
public sealed record DeclaredDamlName(DamlSymbolKind Kind, string? PackageId, string? Module, string Entity, string? Member, string Namespace);
