using Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis.Text;

namespace Daml.VisualStudio.Roslyn.Bindings;

/// <summary>
/// One C# declaration the generator wrote for a Daml declaration, with the Daml names the code
/// itself states. Templates and interfaces state their package and module; data types and their
/// members don't, so those stay null until the generation the file belongs to supplies them.
/// </summary>
/// <param name="Kind">What the Daml declaration is.</param>
/// <param name="PackageId">The package, from <c>TemplateId</c> or <c>InterfaceId</c>; null for data types.</param>
/// <param name="Module">The module, likewise.</param>
/// <param name="Entity">The template, interface or data type.</param>
/// <param name="Member">The choice, field or constructor, as <see cref="DamlSymbolId.Member"/> has it; null for entities.</param>
/// <param name="Namespace">The C# namespace the generated type is in.</param>
/// <param name="DeclarationId">The C# declaration, as <c>DocumentationCommentId</c> names it.</param>
/// <param name="FilePath">The file with the declaration.</param>
/// <param name="Span">Its name in that file.</param>
public sealed record GeneratedDeclaration(
    DamlSymbolKind Kind,
    string? PackageId,
    string? Module,
    string Entity,
    string? Member,
    string Namespace,
    string DeclarationId,
    string FilePath,
    TextSpan Span);
