using System.ComponentModel.Composition;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace Equilibrium.Daml.VisualStudio.Navigation;

/// <summary>Gives Daml buffers word boundaries that keep primes in names.</summary>
[Export(typeof(ITextStructureNavigatorProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
internal sealed class DamlTextStructureNavigatorProvider : ITextStructureNavigatorProvider
{
    private readonly ITextStructureNavigatorSelectorService _selector;
    private readonly IContentTypeRegistryService _contentTypes;

    [ImportingConstructor]
    public DamlTextStructureNavigatorProvider(ITextStructureNavigatorSelectorService selector, IContentTypeRegistryService contentTypes)
    {
        _selector = selector;
        _contentTypes = contentTypes;
    }

    public ITextStructureNavigator CreateTextStructureNavigator(ITextBuffer textBuffer) =>
        new DamlTextStructureNavigator(textBuffer, _selector.CreateTextStructureNavigator(textBuffer, _contentTypes.GetContentType("text")));
}

/// <summary>
/// Double-click and word-wise moves follow Daml's idea of a word; everything else (enclosing
/// spans, siblings) is the plain text navigator's.
/// </summary>
internal sealed class DamlTextStructureNavigator : ITextStructureNavigator
{
    private readonly ITextBuffer _buffer;
    private readonly ITextStructureNavigator _text;

    public DamlTextStructureNavigator(ITextBuffer buffer, ITextStructureNavigator text)
    {
        _buffer = buffer;
        _text = text;
    }

    public IContentType ContentType => _buffer.ContentType;

    public TextExtent GetExtentOfWord(SnapshotPoint currentPosition)
    {
        var line = currentPosition.GetContainingLine();
        if (currentPosition.Position >= line.End.Position)
        {
            return _text.GetExtentOfWord(currentPosition);
        }
        var word = DamlWords.WordAt(line.GetText(), currentPosition.Position - line.Start.Position);
        return new TextExtent(new SnapshotSpan(line.Start + word.Start, word.Length), word.IsSignificant);
    }

    public SnapshotSpan GetSpanOfEnclosing(SnapshotSpan activeSpan) => _text.GetSpanOfEnclosing(activeSpan);

    public SnapshotSpan GetSpanOfFirstChild(SnapshotSpan activeSpan) => _text.GetSpanOfFirstChild(activeSpan);

    public SnapshotSpan GetSpanOfNextSibling(SnapshotSpan activeSpan) => _text.GetSpanOfNextSibling(activeSpan);

    public SnapshotSpan GetSpanOfPreviousSibling(SnapshotSpan activeSpan) => _text.GetSpanOfPreviousSibling(activeSpan);
}
