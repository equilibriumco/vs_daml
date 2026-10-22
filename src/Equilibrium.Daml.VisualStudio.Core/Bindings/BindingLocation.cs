using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>
/// One C# declaration standing for a Daml symbol in one project. The declaration is named by its
/// <c>DocumentationCommentId</c>, which is resolved again in the project's current compilation
/// before it is used; the place in the file is where it was when the index read it.
/// </summary>
/// <param name="Symbol">The Daml symbol.</param>
/// <param name="ProjectKey">The project, as the host identifies it.</param>
/// <param name="TargetFramework">The project's target framework when it builds for several; null otherwise.</param>
/// <param name="Generation">The generation the file belongs to.</param>
/// <param name="DeclarationId">The C# declaration.</param>
/// <param name="FilePath">The file with the declaration.</param>
/// <param name="Start">Where the declaration's name starts in the file.</param>
/// <param name="Length">How long the name is.</param>
public sealed record BindingLocation(
    DamlSymbolId Symbol,
    string ProjectKey,
    string? TargetFramework,
    GenerationProvenance Generation,
    string DeclarationId,
    string FilePath,
    int Start,
    int Length);

/// <summary>
/// A physical C# declaration of a Daml symbol: one place in one file, with every project that
/// compiles it, as a file linked into several projects is.
/// </summary>
/// <param name="FilePath">The file.</param>
/// <param name="DeclarationId">The C# declaration.</param>
/// <param name="Start">Where its name starts.</param>
/// <param name="Length">How long the name is.</param>
/// <param name="Locations">The declaration in each project that has the file.</param>
public sealed record BindingDeclaration(string FilePath, string DeclarationId, int Start, int Length, IReadOnlyList<BindingLocation> Locations);
