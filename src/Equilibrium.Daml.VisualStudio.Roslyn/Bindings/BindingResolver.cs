using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Bindings;

/// <summary>What an index location is in the solution as it is now: the C# symbol, or why there is none.</summary>
/// <param name="Symbol">The C# symbol; null when the location no longer holds.</param>
/// <param name="Problem">Why not, when <paramref name="Symbol"/> is null.</param>
public sealed record ResolvedBinding(ISymbol? Symbol, string? Problem);

/// <summary>
/// Finds the C# symbol an index location names, by its <c>DocumentationCommentId</c> in the
/// project's current compilation, and makes sure it still stands for the location's Daml symbol:
/// finding a symbol of that name doesn't prove it, as the rest of a partial type outlives the part
/// that declared its template. A file the editor holds from the build before is checked as the
/// generator wrote it.
/// </summary>
public static class BindingResolver
{
    /// <summary>The symbol <paramref name="location"/> names in <paramref name="solution"/>.</summary>
    public static async Task<ResolvedBinding> ResolveAsync(Solution solution, BindingLocation location, GenerationCatalog catalog, CancellationToken cancellationToken = default)
    {
        if (BindingIndexBuilder.Project(solution, location.ProjectKey) is not { } project)
        {
            return new ResolvedBinding(null, "The project that had it isn't in the solution any more.");
        }
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null || DocumentationCommentId.GetFirstSymbolForDeclarationId(location.DeclarationId, compilation) is not { } symbol)
        {
            return new ResolvedBinding(null, $"{location.DeclarationId} isn't in {project.Name} any more; build the project to bring its Daml bindings up to date.");
        }
        foreach (var tree in symbol.DeclaringSyntaxReferences.Select(r => r.SyntaxTree).Distinct())
        {
            if (StandsFor(BindingReader.Read(compilation.GetSemanticModel(tree), catalog, cancellationToken), location))
            {
                return new ResolvedBinding(symbol, null);
            }
            // The editor's copy may be of the build before, while the index read what the generator wrote.
            if (project.GetDocument(tree) is { } document
                && await WrittenCode.ModelAsync(document, cancellationToken).ConfigureAwait(false) is { } written
                && StandsFor(BindingReader.Read(written, catalog, cancellationToken), location))
            {
                return new ResolvedBinding(symbol, null);
            }
        }
        return new ResolvedBinding(null, $"{location.DeclarationId} no longer stands for {location.Symbol}; build the project to bring its Daml bindings up to date.");
    }

    private static bool StandsFor(DocumentBindings read, BindingLocation location) =>
        read.Bindings.Any(b => b.DeclarationId == location.DeclarationId && b.Symbol == location.Symbol);
}
