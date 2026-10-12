using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

// Comment toggling and auto-closing come from the language configuration file, so the only real
// proof is the editor itself: commands go through the view's command chain like keystrokes do.
public class LanguageConfigurationTests
{
    private const string Text = "module Scratch where\n\nanswer : Int\nanswer = 42\n";
    private const int VariantSize = 24;

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Comment_selection_prefixes_the_selected_lines_with_double_dashes()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (textView, view) = OpenScratch();
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

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Typing_an_opening_parenthesis_adds_the_closing_one()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (textView, view) = OpenScratch();
        var line = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(3);
        view.Caret.MoveTo(line.End);

        TypeChar(textView, ' ');
        TypeChar(textView, '(');

        Assert.Equal("answer = 42 ()", view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(3).GetText());
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Typing_an_apostrophe_after_a_name_adds_nothing_else()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var (textView, view) = OpenScratch();
        var line = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2);
        view.Caret.MoveTo(line.Start + "answer".Length);

        TypeChar(textView, '\'');

        Assert.Equal("answer' : Int", view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(2).GetText());
    }

    // A fresh copy per test, so edits never touch a checked-in file.
    private static (IVsTextView TextView, IWpfTextView View) OpenScratch()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var folder = Path.Combine(Path.GetTempPath(), "daml-vs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Scratch.daml");
        File.WriteAllText(file, Text);

        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file, Guid.Empty, out _, out _, out _, out IVsTextView textView);

        var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("SComponentModel is not available.");
        var view = componentModel.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(textView) ?? throw new InvalidOperationException("No WPF view for the scratch file.");
        Assert.Equal("daml", view.TextBuffer.ContentType.TypeName);
        return (textView, view);
    }

    private static void Exec(IVsTextView textView, VSConstants.VSStd2KCmdID command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var group = VSConstants.VSStd2K;
        var result = ((IOleCommandTarget)textView).Exec(ref group, (uint)command, 0, IntPtr.Zero, IntPtr.Zero);
        Assert.Equal(VSConstants.S_OK, result);
    }

    private static void TypeChar(IVsTextView textView, char c)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var group = VSConstants.VSStd2K;
        // A VARIANT is 24 bytes in a 64-bit process; writing one into less corrupts the heap and
        // the CLR takes Visual Studio down some time later.
        var variant = Marshal.AllocCoTaskMem(VariantSize);
        try
        {
            Marshal.GetNativeVariantForObject(c, variant);
            var result = ((IOleCommandTarget)textView).Exec(ref group, (uint)VSConstants.VSStd2KCmdID.TYPECHAR, 0, variant, IntPtr.Zero);
            Assert.Equal(VSConstants.S_OK, result);
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant);
        }
    }
}
