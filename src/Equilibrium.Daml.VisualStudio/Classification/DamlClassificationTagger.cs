using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Equilibrium.Daml.VisualStudio.Editing;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Equilibrium.Daml.VisualStudio.Classification;

/// <summary>Hands out the one classification tagger each Daml buffer has.</summary>
[Export(typeof(ITaggerProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TagType(typeof(IClassificationTag))]
internal sealed class DamlClassificationTaggerProvider : ITaggerProvider
{
    private readonly IClassificationTypeRegistryService _registry;

    [ImportingConstructor]
    public DamlClassificationTaggerProvider(IClassificationTypeRegistryService registry) => _registry = registry;

    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer)
        where T : ITag =>
        buffer.Properties.GetOrCreateSingletonProperty(() => new DamlClassificationTagger(buffer, _registry)) as ITagger<T>;
}

/// <summary>
/// Colours a Daml buffer from the tokens every other feature of the buffer shares. After an edit
/// only the lines the token cache lexed again are reported, e.g. every line below a newly opened
/// block comment, but nothing past the point where the lexer state agrees with the old one again.
/// </summary>
internal sealed class DamlClassificationTagger : ITagger<IClassificationTag>
{
    private readonly object _gate = new();
    private readonly IClassificationTypeRegistryService _registry;
    private readonly Dictionary<string, IClassificationTag> _tags = new(StringComparer.Ordinal);
    private readonly DamlTokenCache _tokens;

    public DamlClassificationTagger(ITextBuffer buffer, IClassificationTypeRegistryService registry)
    {
        _registry = registry;
        _tokens = DamlTokenCache.For(buffer);
        buffer.Changed += OnBufferChanged;
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public IEnumerable<ITagSpan<IClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        var result = new List<ITagSpan<IClassificationTag>>();
        if (spans.Count == 0)
        {
            return result;
        }

        // An older snapshot than the cache's is lexed on its own, so no paint goes uncoloured.
        var snapshot = spans[0].Snapshot;
        var lines = _tokens.Get(snapshot);
        var lastLine = -1;
        foreach (var span in spans)
        {
            var first = Math.Max(snapshot.GetLineNumberFromPosition(span.Start), lastLine + 1);
            var last = snapshot.GetLineNumberFromPosition(span.End);
            for (var number = first; number <= last; number++)
            {
                var start = snapshot.GetLineFromLineNumber(number).Start;
                foreach (var classified in ClassificationMapper.SpansOf(lines[number]))
                {
                    result.Add(new TagSpan<IClassificationTag>(new SnapshotSpan(snapshot, start + classified.Start, classified.Length), Tag(classified.Classification)));
                }
            }
            lastLine = last;
        }
        return result;
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        // Brings the cache to this snapshot now, on the editor's thread, so the paint that follows
        // finds it there; another feature may have got there first, in which case this is free.
        _tokens.Get(e.After);
        var changed = _tokens.Relexed(e.Before, e.After) ?? new LineRange(0, e.After.LineCount - 1);
        if (!changed.IsEmpty)
        {
            var start = e.After.GetLineFromLineNumber(changed.FirstLine).Start;
            var end = e.After.GetLineFromLineNumber(changed.LastLine).EndIncludingLineBreak;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(start, end)));
        }
    }

    private IClassificationTag Tag(string classification)
    {
        lock (_gate)
        {
            if (!_tags.TryGetValue(classification, out var tag))
            {
                tag = new ClassificationTag(_registry.GetClassificationType(classification));
                _tags[classification] = tag;
            }
            return tag;
        }
    }
}
