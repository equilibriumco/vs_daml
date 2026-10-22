using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

public class SnippetTests
{
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Typing_templ_offers_the_template_snippet_which_inserts_with_its_name_selected()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (_, view) = Ide.OpenScratch("module Scratch where\n\n");
        try
        {
            view.Caret.MoveTo(view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2).Start);

            foreach (var c in "templ")
            {
                Ide.TypeChar(view, c);
            }

            var broker = Ide.ComponentModel().GetService<IAsyncCompletionBroker>();
            var session = broker.GetSession(view);
            Assert.NotNull(session);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var items = session!.GetComputedItems(timeout.Token);
            Assert.Contains(items.Items, i => i.DisplayText == "template");

            Assert.Equal("template", items.SelectedItem?.DisplayText);
            session.Commit('\t', timeout.Token);

            var text = view.TextBuffer.CurrentSnapshot.GetText();
            Assert.Contains("template MyTemplate\n  with\n    owner : Party\n  where\n    signatory owner\n", text.Replace("\r\n", "\n"));
            Assert.Equal("MyTemplate", view.Selection.SelectedSpans.Single().GetText());
        }
        finally
        {
            Ide.CloseAllDocuments();
        }
    }

    // A choice compiles only among a template's declarations; typed at the top level it would be
    // a declaration Daml doesn't have.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task The_choice_snippet_is_offered_in_a_template_and_not_at_the_top_level()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (_, view) = Ide.OpenScratch("module Scratch where\n\ntemplate Asset\n  with\n    owner : Party\n  where\n    signatory owner\n    \n\n");
        try
        {
            Assert.True(OffersChoice(view, line: 7, column: 4), "no choice snippet among the template's declarations");
            Assert.False(OffersChoice(view, line: 9, column: 0), "a choice snippet at the top level");
        }
        finally
        {
            Ide.CloseAllDocuments();
        }
    }

    private static bool OffersChoice(Microsoft.VisualStudio.Text.Editor.IWpfTextView view, int line, int column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var start = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(line).Start.Position + column;
        view.Caret.MoveTo(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, start));
        foreach (var c in "cho")
        {
            Ide.TypeChar(view, c);
        }
        var session = Ide.ComponentModel().GetService<IAsyncCompletionBroker>().GetSession(view);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var offered = session is not null && session.GetComputedItems(timeout.Token).Items.Any(i => i.DisplayText == "choice");
        session?.Dismiss();
        view.TextBuffer.Delete(new Span(start, 3));
        return offered;
    }
}
