using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class SnippetTests
{
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Typing_templ_offers_the_template_snippet_which_inserts_with_its_name_selected()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (_, view) = Ide.OpenScratch("module Scratch where\n\n");
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
}
