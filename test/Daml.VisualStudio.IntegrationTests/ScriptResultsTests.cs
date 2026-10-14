using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

// The way to script results a user takes: the row above a script in the editor, then the
// results window it opens.
public class ScriptResultsTests
{
    private const string LensLayer = "DamlLensRows";
    private const int ShowScriptResultsAtCaret = 0x0205;
    private static readonly TimeSpan ServerStart = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task The_script_gets_a_results_row_that_opens_the_results_window()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var folder = Ide.CopySample("single-package");
        Ide.OpenFolder(folder);
        try
        {
            var view = Ide.OpenFile(Path.Combine(folder, "daml", "Main.daml"));
            await Ide.WaitForServerAsync(ready: true, ServerStart);
            var setup = view.TextBuffer.CurrentSnapshot.Lines.First(l => l.GetText().StartsWith("setup", StringComparison.Ordinal));

            Assert.True(await RowDrawnAboveAsync(view, setup.LineNumber), "No lens row appeared above setup within 60 s of the server being ready.");

            view.Caret.MoveTo(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(setup.LineNumber + 1).Start));
            Ide.RunDamlCommand(ShowScriptResultsAtCaret);

            Assert.True(await WindowCaptionedAsync("Script: setup"), "No results window titled 'Script: setup' opened.");
        }
        finally
        {
            Ide.CloseAllDocuments();
            await Ide.CloseFolderAsync();
        }
    }

    // The row is an adornment in its own layer, placed over the line it belongs to.
    private static async Task<bool> RowDrawnAboveAsync(IWpfTextView view, int lineNumber)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var layer = view.GetAdornmentLayer(LensLayer);
            if (layer.Elements.Any(e => e.VisualSpan is { } span && span.Snapshot.GetLineNumberFromPosition(span.Start) == lineNumber))
            {
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    private static async Task<bool> WindowCaptionedAsync(string caption)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell ?? throw new InvalidOperationException("SVsUIShell is not available.");
            ErrorHandler.ThrowOnFailure(shell.GetToolWindowEnum(out var frames));
            var frame = new IVsWindowFrame[1];
            while (frames.Next(1, frame, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                if (ErrorHandler.Succeeded(frame[0].GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var title)) && Equals(title, caption))
                {
                    return true;
                }
            }
            await Task.Delay(500);
        }
        return false;
    }
}
