using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
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
        try
        {
            var line = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2);
            view.Caret.MoveTo(line.Start + "f a = ".Length);
            var marks = await MarksAsync(view, "bracehighlight", marks => marks.Length == 2);
            Assert.Equal(["(", ")"], marks.Select(mark => mark.GetText()));
            Assert.Equal([line.Start.Position + 6, line.End.Position - 1], marks.Select(mark => mark.Start.Position));
        }
        finally { Ide.CloseAllDocuments(); }
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task The_caret_on_a_name_highlights_every_use_of_it()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (_, view) = Ide.OpenScratch(Text);
        try
        {
            var line = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2);
            view.Caret.MoveTo(line.Start + 2);
            var marks = await MarksAsync(view, "MarkerFormatDefinition/HighlightedReference", marks => marks.Count(mark => mark.GetText() == "a") == 3);
            Assert.Equal(3, marks.Count(mark => mark.GetText() == "a"));
        }
        finally { Ide.CloseAllDocuments(); }
    }

    private static async Task<SnapshotSpan[]> MarksAsync(IWpfTextView view, string type, Func<SnapshotSpan[], bool> ready)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        using var tags = Ide.ComponentModel().GetService<IViewTagAggregatorFactoryService>().CreateTagAggregator<TextMarkerTag>(view);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        do
        {
            var snapshot = view.TextBuffer.CurrentSnapshot;
            var marks = tags.GetTags(new SnapshotSpan(snapshot, 0, snapshot.Length))
                .Where(tag => tag.Tag.Type == type)
                .SelectMany(tag => tag.Span.GetSpans(snapshot)).OrderBy(span => span.Start.Position).ToArray();
            if (ready(marks)) { return marks; }
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        Assert.Fail($"Caret markers of type {type} did not become ready.");
        return [];
    }
}
