using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Bindings;

/// <summary>Adds the Daml a C# name stands for to the C# quick info.</summary>
[Export(typeof(IAsyncQuickInfoSourceProvider))]
[Name("Daml bindings")]
[ContentType("CSharp")]
internal sealed class DamlQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
{
    private readonly DamlBindingService _service;
    private readonly DamlNavigator _navigator;

    [ImportingConstructor]
    public DamlQuickInfoSourceProvider(DamlBindingService service, DamlNavigator navigator)
    {
        _service = service;
        _navigator = navigator;
    }

    public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
        textBuffer.Properties.GetOrCreateSingletonProperty(() => new DamlQuickInfoSource(textBuffer, _service, _navigator));
}

/// <summary>
/// <c>Daml: Kinds.Asset:Asset — Go to Daml definition</c> under the C# quick info of a name that
/// stands for Daml, the link doing what the command does.
/// </summary>
internal sealed class DamlQuickInfoSource : IAsyncQuickInfoSource
{
    private readonly ITextBuffer _buffer;
    private readonly DamlBindingService _service;
    private readonly DamlNavigator _navigator;

    public DamlQuickInfoSource(ITextBuffer buffer, DamlBindingService service, DamlNavigator navigator)
    {
        _buffer = buffer;
        _service = service;
        _navigator = navigator;
    }

    public async Task<QuickInfoItem?> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
    {
        if (session.GetTriggerPoint(_buffer.CurrentSnapshot) is not { } point)
        {
            return null;
        }
        await TaskScheduler.Default;
        if (point.Snapshot.GetOpenDocumentInCurrentContextWithChanges() is not { } document
            || await _service.FindAsync(document, point.Position, cancellationToken) is not { } found)
        {
            return null;
        }
        var lines = found.Found.Symbols.Select(symbol => new ClassifiedTextElement(
            new ClassifiedTextRun(PredefinedClassificationTypeNames.Text, "Daml: "),
            new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, DamlNavigationPlanner.Name(symbol)),
            new ClassifiedTextRun(PredefinedClassificationTypeNames.Text, " — "),
            new ClassifiedTextRun(PredefinedClassificationTypeNames.Text, "Go to Daml definition", () => _navigator.Go(found.Found, found.Index), "Opens the Daml declaration", ClassifiedTextRunStyle.Underline)));
        var span = point.Snapshot.CreateTrackingSpan(found.Found.Span.Start, found.Found.Span.Length, SpanTrackingMode.EdgeInclusive);
        return new QuickInfoItem(span, new ContainerElement(ContainerElementStyle.Stacked, lines));
    }

    public void Dispose()
    {
    }
}
