using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.Web.WebView2.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace Daml.VisualStudio.IntegrationTests;

// The way to script results a user takes: the row above a script in the editor, the results
// window it opens, and what the page in that window can and can't do.
public class ScriptResultsTests
{
    private const string LensLayer = "DamlLensRows";
    private const int ShowScriptResultsAtCaret = 0x0205;
    private static readonly TimeSpan ServerStart = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public ScriptResultsTests(ITestOutputHelper output) => _output = output;

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task The_script_gets_a_results_row_that_opens_the_results_window()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ResultsWindow.CloseAll();
        var folder = Ide.CopySample("single-package");
        Ide.OpenFolder(folder);
        try
        {
            var view = await OpenWithRowsAsync(Path.Combine(folder, "daml", "Main.daml"), expectedRows: 1);
            await ShowResultsOfAsync(view, "setup");

            Assert.True(await ResultsWindow.WaitForAsync("Script: setup") is not null, "No results window titled 'Script: setup' opened. " + await DiagnosticsAsync(view));
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResultsWindow.CloseAll();
            Ide.CloseAllDocuments();
            await Ide.CloseFolderAsync();
        }
    }

    // A closed folder's results can't refresh and their links lead nowhere, and a script of the
    // same name in the next folder would get a second window with the same title.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Closing_the_folder_closes_its_results_windows()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ResultsWindow.CloseAll();
        var folder = Ide.CopySample("single-package");
        Ide.OpenFolder(folder);
        var folderOpen = true;
        try
        {
            var view = await OpenWithRowsAsync(Path.Combine(folder, "daml", "Main.daml"), expectedRows: 1);
            await ShowResultsOfAsync(view, "setup");
            var browser = await BrowserAsync("Script: setup", view);
            Assert.Contains("TV", await ResultsWindow.WaitForTextAsync(browser, t => t.Contains("TV")));

            Ide.CloseAllDocuments();
            await Ide.CloseFolderAsync();
            folderOpen = false;

            var gone = await ResultsWindow.WaitForGoneAsync("Script: setup", TimeSpan.FromSeconds(15));
            var left = ResultsWindow.Find("Script: setup");
            Assert.True(gone, $"The results window stayed open after its folder closed (visible: {left?.IsVisible() == VSConstants.S_OK}). " + await DiagnosticsAsync(null));
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResultsWindow.CloseAll();
            if (folderOpen)
            {
                Ide.CloseAllDocuments();
                await Ide.CloseFolderAsync();
            }
        }
    }

    // Edit the script, save: the open window shows the new results without taking the focus.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 8 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Results_show_the_contracts_and_follow_an_edit_without_taking_the_focus()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ResultsWindow.CloseAll();
        var folder = Ide.CopySample("single-package");
        Ide.OpenFolder(folder);
        try
        {
            var main = Path.Combine(folder, "daml", "Main.daml");
            var view = await OpenWithRowsAsync(main, expectedRows: 1);
            await ShowResultsOfAsync(view, "setup");
            var browser = await BrowserAsync("Script: setup", view);

            var before = await ResultsWindow.WaitForTextAsync(browser, t => t.Contains("TV"));
            Assert.Contains("TV", before);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Ide.Dte().Documents.Item("Main.daml").Activate();
            var snapshot = view.TextBuffer.CurrentSnapshot;
            var at = snapshot.GetText().IndexOf("\"TV\"", StringComparison.Ordinal);
            view.TextBuffer.Replace(new Span(at, 4), "\"Radio\"");
            Ide.Dte().Documents.Item("Main.daml").Save();

            var after = await ResultsWindow.WaitForTextAsync(browser, t => t.Contains("Radio"));
            Assert.Contains("Radio", after);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Assert.Equal("Main.daml", Ide.Dte().ActiveWindow?.Document?.Name);
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResultsWindow.CloseAll();
            Ide.CloseAllDocuments();
            await Ide.CloseFolderAsync();
        }
    }

    // Ten windows at once, then twenty open/close cycles of one: the browser processes of the
    // extension's profile don't pile up, and the memory of Visual Studio stays within reason.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 15 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Many_windows_and_open_close_cycles_leave_no_browser_processes_behind()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ResultsWindow.CloseAll();
        var folder = Ide.CopySample("single-package");
        var scripts = Path.Combine(folder, "daml", "Scripts.daml");
        var source = new StringBuilder("module Scripts where\n\nimport Daml.Script\n");
        for (var i = 1; i <= 10; i++)
        {
            source.Append($"\ns{i} : Script ()\ns{i} = pure ()\n");
        }
        File.WriteAllText(scripts, source.ToString());
        Ide.OpenFolder(folder);
        try
        {
            var view = await OpenWithRowsAsync(scripts, expectedRows: 10);
            for (var i = 1; i <= 10; i++)
            {
                await ShowResultsOfAsync(view, $"s{i}");
                Assert.True(await ResultsWindow.WaitForAsync($"Script: s{i}") is not null, $"No results window titled 'Script: s{i}' opened. " + await DiagnosticsAsync(view));
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // Exactly these ten: "Script: setup" windows of earlier tests start the same way.
            var ten = new HashSet<string>(Enumerable.Range(1, 10).Select(i => $"Script: s{i}"));
            Assert.Equal(10, ResultsWindow.CountCaptionedLike(ten.Contains));
            var withTen = ResultsWindow.BrowserProcesses();
            for (var i = 1; i <= 10; i++)
            {
                ResultsWindow.Close(ResultsWindow.Find($"Script: s{i}")!);
            }

            await ShowResultsOfAsync(view, "s1");
            await BrowserAsync("Script: s1", view);
            var withOne = ResultsWindow.BrowserProcesses();
            var memoryBefore = ResultsWindow.DevenvPrivateBytes();
            for (var cycle = 0; cycle < 20; cycle++)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ResultsWindow.Close(ResultsWindow.Find("Script: s1")!);
                await ShowResultsOfAsync(view, "s1");
                await BrowserAsync("Script: s1", view);
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
            var afterCycles = ResultsWindow.BrowserProcesses();
            var growth = (ResultsWindow.DevenvPrivateBytes() - memoryBefore) / (1024 * 1024);
            _output.WriteLine($"browser processes: ten windows {withTen}, one window {withOne}, after 20 cycles {afterCycles}; devenv private bytes +{growth} MB");

            Assert.True(afterCycles <= withOne + 1, $"Browser processes grew from {withOne} to {afterCycles} over 20 open/close cycles.");
            Assert.True(growth < 300, $"Visual Studio grew by {growth} MB over 20 open/close cycles.");
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResultsWindow.CloseAll();
            Ide.CloseAllDocuments();
            await Ide.CloseFolderAsync();
        }
    }

    // A source link in the results, clicked with the mouse, opens the script's file at the
    // linked lines. This is also how we learn that the browser reports such a click as the
    // user's: if it didn't, the link would be refused.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task Clicking_a_source_link_in_the_results_opens_the_source()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ResultsWindow.CloseAll();
        var folder = Ide.CopySample("single-package");
        Ide.OpenFolder(folder);
        try
        {
            var main = Path.Combine(folder, "daml", "Main.daml");
            var view = await OpenWithRowsAsync(main, expectedRows: 1);
            await ShowResultsOfAsync(view, "setup");
            var browser = await BrowserAsync("Script: setup", view);
            await ResultsWindow.WaitForTextAsync(browser, t => t.Contains("TV"));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            view.Selection.Clear();
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(ResultsWindow.Find("Script: setup")!.Show());
            // The source links are in the transaction view; the table, shown first, has none.
            await ResultsWindow.EvalAsync(browser, "if (!document.body.classList.contains('hide_table')) toggle_view(); true");
            // What the mouse lands on, so a miss can be told from a link the window refused.
            await ResultsWindow.EvalAsync(browser, "window.clickedOn = 'nothing'; document.addEventListener('click', e => { window.clickedOn = (e.target.closest('a') || e.target).outerHTML.slice(0, 160); }, true); true");

            Assert.True(await ResultsWindow.ClickAsync(browser, "a[href^='command:daml.revealLocation']"), "The results have no source link.");

            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline && view.Selection.IsEmpty)
            {
                await Task.Delay(250);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            // The Daml pane says whether the window refused the link (not a click, outside the
            // workspace) or the source didn't open.
            var diagnostics = view.Selection.IsEmpty
                ? $"The click landed on: {await ResultsWindow.EvalAsync(browser, "window.clickedOn")}; the click measured {ResultsWindow.LastClick}. " + await DiagnosticsAsync(view)
                : "";
            Assert.False(view.Selection.IsEmpty, "Clicking the source link selected nothing in Main.daml. " + diagnostics);
            Assert.Equal("Main.daml", Ide.Dte().ActiveWindow?.Document?.Name);
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResultsWindow.CloseAll();
            Ide.CloseAllDocuments();
            await Ide.CloseFolderAsync();
        }
    }

    // The view chosen on the page (transactions instead of the table) is the user's choice for
    // that script: closing the window and opening it again brings it back, and it is kept in the
    // workspace's .vs folder for the next session.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 8 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task The_chosen_view_comes_back_when_the_window_opens_again()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ResultsWindow.CloseAll();
        var folder = Ide.CopySample("single-package");
        Ide.OpenFolder(folder);
        try
        {
            var view = await OpenWithRowsAsync(Path.Combine(folder, "daml", "Main.daml"), expectedRows: 1);
            await ShowResultsOfAsync(view, "setup");
            var browser = await BrowserAsync("Script: setup", view);
            await ResultsWindow.WaitForTextAsync(browser, t => t.Contains("TV"));
            Assert.Equal(false, (bool?)await ResultsWindow.EvalAsync(browser, "document.body.classList.contains('hide_table')"));

            // The page's own switch, as its button would call it.
            await ResultsWindow.EvalAsync(browser, "toggle_view()");
            var saved = Path.Combine(folder, ".vs", "DamlVisualStudio", "view-state.json");
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline && !(File.Exists(saved) && File.ReadAllText(saved).Contains("transaction")))
            {
                await Task.Delay(250);
            }
            Assert.True(File.Exists(saved) && File.ReadAllText(saved).Contains("transaction"), $"The chosen view wasn't saved to {saved}.");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResultsWindow.Close(ResultsWindow.Find("Script: setup")!);
            await ShowResultsOfAsync(view, "setup");
            var reopened = await BrowserAsync("Script: setup", view);
            await ResultsWindow.WaitForTextAsync(reopened, t => t.Contains("TV"));

            deadline = DateTime.UtcNow + Patience;
            bool? transaction = false;
            while (DateTime.UtcNow < deadline && transaction != true)
            {
                transaction = (bool?)await ResultsWindow.EvalAsync(reopened, "document.body.classList.contains('hide_table')");
                await Task.Delay(250);
            }
            Assert.True(transaction, "The reopened window shows the table, not the transactions chosen before.");
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResultsWindow.CloseAll();
            Ide.CloseAllDocuments();
            await Ide.CloseFolderAsync();
        }
    }

    // The page is shown, not trusted: it can't fetch from the network, open windows or go to
    // another address, whatever its script tries.
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18, Timeout = 6 * 60), Trait("Category", "Integration"), Trait("RequiresDaml", "true")]
    public async Task The_results_page_can_reach_nothing_outside_itself()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ResultsWindow.CloseAll();
        var folder = Ide.CopySample("single-package");
        Ide.OpenFolder(folder);
        try
        {
            var view = await OpenWithRowsAsync(Path.Combine(folder, "daml", "Main.daml"), expectedRows: 1);
            await ShowResultsOfAsync(view, "setup");
            var browser = await BrowserAsync("Script: setup", view);
            await ResultsWindow.WaitForTextAsync(browser, t => t.Contains("TV"));
            var page = (string?)await ResultsWindow.EvalAsync(browser, "location.href");

            await ResultsWindow.EvalAsync(browser, "window.__fetch = 'pending'; fetch('https://example.com/').then(r => window.__fetch = 'status ' + r.status, () => window.__fetch = 'refused');");
            Assert.Equal("null", (string?)await ResultsWindow.EvalAsync(browser, "String(window.open('https://example.com/'))"));
            await ResultsWindow.EvalAsync(browser, "location.href = 'https://example.com/'");
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.Equal("refused", (string?)await ResultsWindow.EvalAsync(browser, "window.__fetch"));
            Assert.Equal(page, (string?)await ResultsWindow.EvalAsync(browser, "location.href"));
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResultsWindow.CloseAll();
            Ide.CloseAllDocuments();
            await Ide.CloseFolderAsync();
        }
    }

    private static async Task<IWpfTextView> OpenWithRowsAsync(string path, int expectedRows)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var view = Ide.OpenFile(path);
        await Ide.WaitForServerAsync(ready: true, ServerStart);
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // Rows are drawn only for lines on screen; all the samples here fit.
            if (view.GetAdornmentLayer(LensLayer).Elements.Count >= expectedRows)
            {
                return view;
            }
            await Task.Delay(500);
        }
        Assert.Fail($"Only {view.GetAdornmentLayer(LensLayer).Elements.Count} of {expectedRows} lens rows appeared in {Path.GetFileName(path)} within {Patience.TotalSeconds:0} s of the server being ready.");
        return view;
    }

    // The menu command, with the caret in the script's body.
    private static async Task ShowResultsOfAsync(IWpfTextView view, string script)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var snapshot = view.TextBuffer.CurrentSnapshot;
        var declaration = snapshot.Lines.First(l => l.GetText().StartsWith(script + " =", StringComparison.Ordinal) || l.GetText().StartsWith(script + " :", StringComparison.Ordinal));
        Ide.Dte().Documents.Item(Path.GetFileName(view.TextBuffer.Properties.GetProperty<ITextDocument>(typeof(ITextDocument)).FilePath)).Activate();
        view.Caret.MoveTo(declaration.End);
        Ide.RunDamlCommand(ShowScriptResultsAtCaret);
    }

    // What the user would look at when nothing opens: the status bar, where the rows are, and
    // what the extension and Visual Studio logged.
    private static async Task<string> DiagnosticsAsync(IWpfTextView? view)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var rows = view is null
            ? "no view"
            : string.Join(", ", view.GetAdornmentLayer(LensLayer).Elements
                .Where(e => e.VisualSpan is not null)
                .Select(e => e.VisualSpan!.Value.Snapshot.GetLineNumberFromPosition(e.VisualSpan.Value.Start))
                .Distinct());
        return $"Status bar: '{Ide.Dte().StatusBar.Text}'. Rows on lines (0-based): {rows}. "
            + $"Caret on line {(view is null ? -1 : view.Caret.Position.BufferPosition.GetContainingLine().LineNumber)}.{Environment.NewLine}"
            + $"Daml pane:{Environment.NewLine}{Ide.PaneText("Daml", 3000)}{Environment.NewLine}"
            + $"Extensions pane:{Environment.NewLine}{Ide.PaneText("Extensions", 3000)}";
    }

    private static async Task<WebView2> BrowserAsync(string caption, IWpfTextView? view = null)
    {
        var frame = await ResultsWindow.WaitForAsync(caption);
        Assert.True(frame is not null, $"No results window titled '{caption}' opened. " + await DiagnosticsAsync(view));
        var browser = await ResultsWindow.BrowserOfAsync(frame!);
        Assert.True(browser is not null, $"The '{caption}' window has no browser running.");
        return browser!;
    }
}
