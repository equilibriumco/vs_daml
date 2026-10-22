using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Bindings;

/// <summary>A Daml symbol a generated C# declaration stands for, and the generation that ties them.</summary>
/// <param name="Symbol">The Daml symbol.</param>
/// <param name="Generation">Where the C# came from.</param>
/// <param name="DeclarationId">The C# declaration, as <c>DocumentationCommentId</c> names it.</param>
/// <param name="FilePath">The file with the declaration.</param>
/// <param name="Span">Its name in that file.</param>
public sealed record DamlBinding(DamlSymbolId Symbol, GenerationProvenance Generation, string DeclarationId, string FilePath, TextSpan Span);

/// <summary>A generated declaration that can't be trusted to stand for a symbol.</summary>
/// <param name="Declaration">The declaration.</param>
/// <param name="Problem">Why.</param>
public sealed record RejectedDeclaration(GeneratedDeclaration Declaration, string Problem);

/// <summary>What one document's generated declarations stand for.</summary>
/// <param name="Bindings">The declarations tied to Daml symbols.</param>
/// <param name="Rejected">The ones that couldn't be.</param>
public sealed record DocumentBindings(IReadOnlyList<DamlBinding> Bindings, IReadOnlyList<RejectedDeclaration> Rejected)
{
    /// <summary>A document with nothing generated in it.</summary>
    public static DocumentBindings None { get; } = new([], []);
}

/// <summary>
/// Ties a document's generated declarations to Daml symbols through the generation its file
/// belongs to. A file in no generation, or one its manifest doesn't list, has none. A file
/// changed since the generation was published, or a generation that doesn't record its package,
/// gives no symbols at all: nothing would prove what the declarations stand for.
/// </summary>
public static class BindingReader
{
    /// <summary>The bindings of <paramref name="model"/>'s document.</summary>
    public static DocumentBindings Read(SemanticModel model, GenerationCatalog catalog, CancellationToken cancellationToken = default)
    {
        if (catalog.Find(model.SyntaxTree.FilePath) is not { } entry)
        {
            return DocumentBindings.None;
        }
        var declarations = BindingExtractor.Extract(model, cancellationToken);
        if (entry.Provenance is not { } generation)
        {
            return Rejected(declarations, $"The manifest in {entry.Folder} doesn't record which package the C# was generated for; build the project again.");
        }
        if (!entry.Unchanged)
        {
            return Rejected(declarations, $"{model.SyntaxTree.FilePath} changed after it was generated; build the project again to restore it.");
        }
        var bindings = new List<DamlBinding>();
        var rejected = new List<RejectedDeclaration>();
        foreach (var declaration in declarations)
        {
            var resolution = generation.Resolve(declaration.Name);
            if (resolution.Symbol is { } symbol)
            {
                bindings.Add(new DamlBinding(symbol, generation, declaration.DeclarationId, declaration.FilePath, declaration.Span));
            }
            else
            {
                rejected.Add(new RejectedDeclaration(declaration, resolution.Problem!));
            }
        }
        return new DocumentBindings(bindings, rejected);
    }

    private static DocumentBindings Rejected(IReadOnlyList<GeneratedDeclaration> declarations, string problem) =>
        new([], declarations.Select(d => new RejectedDeclaration(d, problem)).ToList());
}
