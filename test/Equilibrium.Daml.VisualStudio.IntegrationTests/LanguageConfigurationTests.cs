using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

// Comment toggling and auto-closing come from the language configuration file, so the only real
// proof is the editor itself: commands go through the view's command chain like keystrokes do.
public class LanguageConfigurationTests
{
    private const string Text = "module Scratch where\n\nanswer : Int\nanswer = 42\n";

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Comment_selection_prefixes_the_selected_lines_with_double_dashes()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (textView, view) = Ide.OpenScratch(Text);
        try
        {
            var snapshot = view.TextBuffer.CurrentSnapshot;
            var selected = new SnapshotSpan(snapshot.GetLineFromLineNumber(2).Start, snapshot.GetLineFromLineNumber(3).End);
            view.Selection.Select(selected, isReversed: false);

            Exec(textView, VSConstants.VSStd2KCmdID.COMMENT_BLOCK);

            var after = view.TextBuffer.CurrentSnapshot;
            Assert.StartsWith("--", after.GetLineFromLineNumber(2).GetText().TrimStart());
            Assert.StartsWith("--", after.GetLineFromLineNumber(3).GetText().TrimStart());
            Assert.Equal("module Scratch where", after.GetLineFromLineNumber(0).GetText());

            Exec(textView, VSConstants.VSStd2KCmdID.UNCOMMENT_BLOCK);

            Assert.Equal(Text, view.TextBuffer.CurrentSnapshot.GetText().Replace("\r\n", "\n"));
        }
        finally
        {
            Ide.CloseAllDocuments();
        }
    }

    // Auto-closing is the editor's own brace completion, not the language configuration:
    // BraceCompletionTests covers it.

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Typing_an_apostrophe_after_a_name_adds_nothing_else()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (textView, view) = Ide.OpenScratch(Text);
        await Ide.WaitForLanguageConfigurationAsync(view);
        try
        {
            var line = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2);
            view.Caret.MoveTo(line.Start + "answer".Length);

            Ide.TypeChar(view, '\'');

            Assert.Equal("answer' : Int", view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2).GetText());
        }
        finally
        {
            Ide.CloseAllDocuments();
        }
    }

    private static void Exec(IVsTextView textView, VSConstants.VSStd2KCmdID command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var group = VSConstants.VSStd2K;
        var result = ((IOleCommandTarget)textView).Exec(ref group, (uint)command, 0, IntPtr.Zero, IntPtr.Zero);
        Assert.Equal(VSConstants.S_OK, result);
    }
}
