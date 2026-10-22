using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

// Closing brackets and quotes is the editor's brace completion with the pairs of the language
// configuration and our switch for Daml views. Typing goes through the command chain like a
// keystroke, so the buffer shows what it did.
public class BraceCompletionTests
{
    private const string Text = "module Scratch where\n\nanswer : Int\nanswer = 42\n";
    private const int Line = 3;

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Typing_an_opening_character_adds_its_partner_and_typing_the_partner_steps_over_it()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (_, view) = Ide.OpenScratch(Text);

        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}'), ('"', '"') })
        {
            var end = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(Line).End;
            view.Caret.MoveTo(end);

            Ide.TypeChar(view, open);

            Assert.Equal($"answer = 42{open}{close}", LineText(view));
            Assert.Equal(end.Position + 1, view.Caret.Position.BufferPosition.Position);

            Ide.TypeChar(view, close);

            Assert.Equal($"answer = 42{open}{close}", LineText(view));
            Assert.Equal(end.Position + 2, view.Caret.Position.BufferPosition.Position);

            view.TextBuffer.Delete(new Span(end.Position, 2));
        }
    }

    // A name may end in an apostrophe, so that one is never paired: the editor would pair it on
    // its own if the configuration listed no pairs at all.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Typing_an_apostrophe_adds_nothing()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (_, view) = Ide.OpenScratch(Text);
        await Ide.WaitForLanguageConfigurationAsync(view);
        view.Caret.MoveTo(view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(Line).End);

        Ide.TypeChar(view, '\'');

        Assert.Equal("answer = 42'", LineText(view));
    }

    private static string LineText(ITextView view) =>
        view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(Line).GetText();
}
