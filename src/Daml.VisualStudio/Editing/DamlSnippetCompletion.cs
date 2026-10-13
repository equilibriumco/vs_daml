using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Snippets;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Editing;

/// <summary>Adds the Daml snippets to completion in Daml views; the language server provides everything else.</summary>
[Export(typeof(IAsyncCompletionSourceProvider))]
[Name("Daml snippets")]
[ContentType(DamlContentDefinition.ContentTypeName)]
internal sealed class DamlSnippetCompletionSourceProvider : IAsyncCompletionSourceProvider
{
    public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
        textView.Properties.GetOrCreateSingletonProperty(() => new DamlSnippetCompletionSource());
}

/// <summary>
/// Offers the snippets for the first word of a line. Committing one is left to
/// <see cref="DamlSnippetCommitManager"/>, which finds the snippet on the item.
/// </summary>
internal sealed class DamlSnippetCompletionSource : IAsyncCompletionSource
{
    /// <summary>The property under which an item carries its <see cref="DamlSnippet"/>.</summary>
    public const string SnippetProperty = "Daml.Snippet";

    private static readonly ImageElement Icon = new(KnownMonikers.Snippet.ToImageId(), "Snippet");

    public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
    {
        var typed = trigger.Reason == CompletionTriggerReason.Insertion;
        if ((typed && !char.IsLetter(trigger.Character))
            || !(typed || trigger.Reason is CompletionTriggerReason.Invoke or CompletionTriggerReason.InvokeAndCommitIfUnique))
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }
        var line = triggerLocation.GetContainingLine();
        // The lines above say whether this one starts inside a comment; the ones below don't matter.
        var lines = LineTokens.Lex(triggerLocation.Snapshot.Lines.Take(line.LineNumber + 1).Select(l => l.GetText()));
        if (SnippetContext.ApplicableSpan(lines, line.LineNumber, triggerLocation.Position - line.Start.Position) is not { } span)
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }
        return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(line.Start + span.Start, span.Length));
    }

    public Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
    {
        var items = DamlSnippets.All.Select(snippet =>
        {
            var item = new CompletionItem(snippet.Shortcut, this, Icon, ImmutableArray<CompletionFilter>.Empty, snippet.Title,
                snippet.Shortcut, snippet.Shortcut, snippet.Shortcut, ImmutableArray<ImageElement>.Empty);
            item.Properties.AddProperty(SnippetProperty, snippet);
            return item;
        });
        return Task.FromResult(new CompletionContext(items.ToImmutableArray()));
    }

    public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token)
    {
        var snippet = item.Properties.GetProperty<DamlSnippet>(SnippetProperty);
        return Task.FromResult<object>(snippet.Description + "\n\n" + SnippetExpander.Expand(snippet.Body, "", "\n").Text);
    }
}

/// <summary>Hands out the snippet commit manager of a Daml view.</summary>
[Export(typeof(IAsyncCompletionCommitManagerProvider))]
[Name("Daml snippets")]
[ContentType(DamlContentDefinition.ContentTypeName)]
internal sealed class DamlSnippetCommitManagerProvider : IAsyncCompletionCommitManagerProvider
{
    public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
        textView.Properties.GetOrCreateSingletonProperty(() => new DamlSnippetCommitManager());
}

/// <summary>
/// Inserts a committed snippet with its default values, indented like its line, and selects the
/// first field so typing replaces it. Items that aren't snippets are left to their own source.
/// </summary>
internal sealed class DamlSnippetCommitManager : IAsyncCompletionCommitManager
{
    public System.Collections.Generic.IEnumerable<char> PotentialCommitCharacters => ImmutableArray<char>.Empty;

    public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => false;

    public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token)
    {
        if (!item.Properties.TryGetProperty(DamlSnippetCompletionSource.SnippetProperty, out DamlSnippet snippet))
        {
            return CommitResult.Unhandled;
        }
        var span = session.ApplicableToSpan.GetSpan(buffer.CurrentSnapshot);
        var line = span.Start.GetContainingLine();
        var indent = line.GetText().Substring(0, span.Start.Position - line.Start.Position);
        var newline = line.LineBreakLength > 0 ? line.GetLineBreakText() : session.TextView.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId);
        var expanded = SnippetExpander.Expand(snippet.Body, indent, newline);

        var after = buffer.Replace(span, expanded.Text);
        var start = span.Start.Position + expanded.Selection.Start;
        session.TextView.Selection.Select(new SnapshotSpan(after, start, expanded.Selection.Length), isReversed: false);
        session.TextView.Caret.MoveTo(new SnapshotPoint(after, start + expanded.Selection.Length));
        return CommitResult.Handled;
    }
}
