using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Classification;

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
/// Colours a Daml buffer from an <see cref="IDamlSyntaxProvider"/>, which it keeps in step with
/// the buffer's edits. After an edit only the lines whose colouring can have changed are
/// reported, e.g. every line below a newly opened block comment, but nothing past the point
/// where the lexer state agrees with the old one again.
/// </summary>
internal sealed class DamlClassificationTagger : ITagger<IClassificationTag>
{
    private readonly object _gate = new();
    private readonly IClassificationTypeRegistryService _registry;
    private readonly Dictionary<string, IClassificationTag> _tags = new(StringComparer.Ordinal);
    private readonly IDamlSyntaxProvider _syntax;
    private ITextSnapshot _snapshot;

    public DamlClassificationTagger(ITextBuffer buffer, IClassificationTypeRegistryService registry)
    {
        _registry = registry;
        _snapshot = buffer.CurrentSnapshot;
        // Kept on the buffer so other features can reuse the same line states.
        _syntax = buffer.Properties.GetOrCreateSingletonProperty<IDamlSyntaxProvider>(() => new LexerSyntaxProvider(new SnapshotLines(_snapshot)));
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

        var snapshot = spans[0].Snapshot;
        lock (_gate)
        {
            if (!CatchUp(snapshot))
            {
                return result; // an older snapshot than ours; the editor will ask again
            }

            var lastLine = -1;
            foreach (var span in spans)
            {
                var first = Math.Max(snapshot.GetLineNumberFromPosition(span.Start), lastLine + 1);
                var last = snapshot.GetLineNumberFromPosition(span.End);
                for (var number = first; number <= last; number++)
                {
                    var line = snapshot.GetLineFromLineNumber(number);
                    foreach (var classified in _syntax.GetClassifications(number))
                    {
                        result.Add(new TagSpan<IClassificationTag>(new SnapshotSpan(snapshot, line.Start + classified.Start, classified.Length), Tag(classified.Classification)));
                    }
                }
                lastLine = last;
            }
        }
        return result;
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        LineRange changed;
        lock (_gate)
        {
            if (e.After.Version.VersionNumber <= _snapshot.Version.VersionNumber)
            {
                return; // already caught up while tagging, possibly past this change
            }
            if (e.Before != _snapshot)
            {
                Reset(e.After);
                changed = new LineRange(0, e.After.LineCount - 1);
            }
            else
            {
                changed = _syntax.ApplyEdit(new SnapshotLines(e.After), ToLineEdit(e));
                _snapshot = e.After;
            }
        }

        if (!changed.IsEmpty)
        {
            var start = e.After.GetLineFromLineNumber(changed.FirstLine).Start;
            var end = e.After.GetLineFromLineNumber(changed.LastLine).EndIncludingLineBreak;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(start, end)));
        }
    }

    // Normally the buffer's Changed event has already brought us to the snapshot being tagged.
    // If tagging gets there first, start over on it; Changed will then find nothing to do.
    private bool CatchUp(ITextSnapshot snapshot)
    {
        if (snapshot == _snapshot)
        {
            return true;
        }
        if (snapshot.TextBuffer != _snapshot.TextBuffer || snapshot.Version.VersionNumber < _snapshot.Version.VersionNumber)
        {
            return false;
        }
        Reset(snapshot);
        return true;
    }

    private void Reset(ITextSnapshot snapshot)
    {
        _syntax.Reset(new SnapshotLines(snapshot));
        _snapshot = snapshot;
    }

    // All of one version's changes as a single line edit: from the first changed line to the
    // last, with the line count difference for matching up the lines below.
    private static LineEdit ToLineEdit(TextContentChangedEventArgs e)
    {
        var changes = e.Changes;
        var firstLine = e.Before.GetLineNumberFromPosition(changes[0].OldPosition);
        var lastLine = e.After.GetLineNumberFromPosition(changes[changes.Count - 1].NewEnd);
        return new LineEdit(firstLine, lastLine, e.After.LineCount - e.Before.LineCount);
    }

    private IClassificationTag Tag(string classification)
    {
        if (!_tags.TryGetValue(classification, out var tag))
        {
            tag = new ClassificationTag(_registry.GetClassificationType(classification));
            _tags[classification] = tag;
        }
        return tag;
    }

    private sealed class SnapshotLines : ILineSource
    {
        private readonly ITextSnapshot _snapshot;

        public SnapshotLines(ITextSnapshot snapshot) => _snapshot = snapshot;

        public int LineCount => _snapshot.LineCount;

        public string GetLineText(int line) => _snapshot.GetLineFromLineNumber(line).GetText();
    }
}
