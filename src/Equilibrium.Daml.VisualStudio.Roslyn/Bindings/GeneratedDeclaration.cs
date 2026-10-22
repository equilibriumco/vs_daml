using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis.Text;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Bindings;

/// <summary>One C# declaration the generator wrote for a Daml declaration.</summary>
/// <param name="Name">The Daml names the code states.</param>
/// <param name="DeclarationId">The C# declaration, as <c>DocumentationCommentId</c> names it.</param>
/// <param name="FilePath">The file with the declaration.</param>
/// <param name="Span">Its name in that file.</param>
public sealed record GeneratedDeclaration(DeclaredDamlName Name, string DeclarationId, string FilePath, TextSpan Span);
