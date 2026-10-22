using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Bindings;

/// <summary>The Daml symbols the C# at a position stands for, and the name there.</summary>
/// <param name="Symbols">The Daml symbols; normally one.</param>
/// <param name="Span">The C# name the position is on.</param>
public sealed record BindingsAtPosition(IReadOnlyList<DamlSymbolId> Symbols, TextSpan Span);

/// <summary>
/// From a place in C# code to the Daml symbols it uses: the C# symbol there, taken back to the
/// generated declaration in whichever project of the solution declares it, then looked up in the
/// index by that project and its <c>DocumentationCommentId</c>. A constructor stands for its type,
/// an accessor or a record's parameter for its property, a constructed generic type for its
/// definition.
/// </summary>
public static class DamlBindingLookup
{
    /// <summary>What the C# at <paramref name="position"/> of <paramref name="document"/> stands for in Daml, or null.</summary>
    public static async Task<BindingsAtPosition?> FindAsync(Document document, int position, BindingIndex index, CancellationToken cancellationToken = default)
    {
        if ((index ?? throw new ArgumentNullException(nameof(index))).Count == 0 || document is null)
        {
            return null;
        }
        if (await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } found
            || Generated(found) is not { } symbol)
        {
            return null;
        }
        var solution = document.Project.Solution;
        var declaration = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, cancellationToken).ConfigureAwait(false) ?? symbol;
        if (declaration.Locations.All(l => !l.IsInSource)
            || solution.GetProject(declaration.ContainingAssembly, cancellationToken) is not { } project
            || DocumentationCommentId.CreateDeclarationId(declaration) is not { } id)
        {
            return null;
        }
        var symbols = index.SymbolsOf(BindingIndexBuilder.ProjectKey(project.Id), id);
        if (symbols.Count == 0)
        {
            return null;
        }
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var token = root!.FindToken(Math.Max(0, Math.Min(position, root.FullSpan.End - 1)));
        if (!token.Span.Contains(position) && position > 0 && root.FindToken(position - 1) is var before && before.Span.End == position)
        {
            token = before;
        }
        return new BindingsAtPosition(symbols, token.Span);
    }

    // The declaration the generator wrote that a use of the symbol refers to.
    private static ISymbol? Generated(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => constructor.ContainingType.OriginalDefinition,
            IMethodSymbol { AssociatedSymbol: IPropertySymbol property } => property.OriginalDefinition,
            IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType.IsRecord: true } constructor } parameter =>
                constructor.ContainingType.OriginalDefinition.GetMembers(parameter.Name).OfType<IPropertySymbol>().FirstOrDefault(),
            IAliasSymbol alias => alias.Target,
            INamedTypeSymbol or IPropertySymbol or IFieldSymbol => symbol.OriginalDefinition,
            _ => null,
        };
}
