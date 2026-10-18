using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Daml.VisualStudio.Roslyn.Bindings;

/// <summary>
/// The C# a user wrote against the bindings of a Daml symbol: every use Find All References finds
/// of each declaration the index has for it, in each project, without uses in generated code (the
/// bindings themselves among it); each place once, however many projects compile it.
/// </summary>
public static class CSharpReferenceFinder
{
    // Enough of a file to see its leading comments.
    private const int Leading = 2000;

    /// <summary>The uses of <paramref name="declarations"/> in <paramref name="solution"/>, in file and line order.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<IReadOnlyList<CSharpReference>> FindAsync(
        Solution solution, IEnumerable<BindingLocation> declarations, GenerationCatalog catalog, CancellationToken cancellationToken = default)
    {
        if (solution is null)
        {
            throw new ArgumentNullException(nameof(solution));
        }
        if (declarations is null)
        {
            throw new ArgumentNullException(nameof(declarations));
        }
        var found = new List<CSharpReference>();
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var generated = new Dictionary<DocumentId, bool>();
        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = await BindingResolver.ResolveAsync(solution, declaration, catalog, cancellationToken).ConfigureAwait(false);
            if (resolved.Symbol is not { } symbol || !symbols.Add(symbol))
            {
                continue;
            }
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
            {
                foreach (var reference in referenced.Locations)
                {
                    if (reference.IsImplicit || !reference.Location.IsInSource)
                    {
                        continue;
                    }
                    var document = reference.Document;
                    var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    if (!generated.TryGetValue(document.Id, out var isGenerated))
                    {
                        generated[document.Id] = isGenerated = CSharpReferences.IsGenerated(document.FilePath, text.ToString(new TextSpan(0, Math.Min(text.Length, Leading))));
                    }
                    if (isGenerated)
                    {
                        continue;
                    }
                    var span = reference.Location.SourceSpan;
                    var line = text.Lines.GetLineFromPosition(span.Start);
                    found.Add(new CSharpReference(document.Project.Name, document.FilePath ?? document.Name, line.LineNumber + 1, span.Start - line.Start + 1, span.Start, span.Length, line.ToString().Trim()));
                }
            }
        }
        return CSharpReferences.Summarize(found);
    }
}
