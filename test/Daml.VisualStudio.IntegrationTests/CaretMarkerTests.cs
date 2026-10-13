using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class CaretMarkerTests
{
    private const string Text = "module Scratch where\n\nf a = (a + g (a))\n";

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task The_caret_at_an_opening_bracket_highlights_it_and_its_partner_once()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (_, view) = Ide.OpenScratch(Text);
        var line = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2);
        view.Caret.MoveTo(line.Start + "f a = ".Length);

        var marks = Marks(view, "bracehighlight");

        // Two marks on `(` would mean the language configuration matches brackets as well.
        Assert.Equal(["(", ")"], marks.Select(m => m.GetText()));
        Assert.Equal([line.Start.Position + 6, line.End.Position - 1], marks.Select(m => m.Start.Position));
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task The_caret_on_a_name_highlights_every_use_of_it()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (_, view) = Ide.OpenScratch(Text);
        var line = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2);
        view.Caret.MoveTo(line.Start + 2);

        var marks = Marks(view, "MarkerFormatDefinition/HighlightedReference");

        Assert.Equal(3, marks.Count(m => m.GetText() == "a"));
    }

    private static SnapshotSpan[] Marks(Microsoft.VisualStudio.Text.Editor.IWpfTextView view, string type)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        using var tags = Ide.ComponentModel().GetService<IViewTagAggregatorFactoryService>().CreateTagAggregator<TextMarkerTag>(view);
        var snapshot = view.TextBuffer.CurrentSnapshot;
        return tags.GetTags(new SnapshotSpan(snapshot, 0, snapshot.Length))
            .Where(t => t.Tag.Type == type)
            .SelectMany(t => t.Span.GetSpans(snapshot))
            .OrderBy(s => s.Start.Position)
            .ToArray();
    }
}
