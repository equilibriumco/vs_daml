using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

// What the IDE tests do to Visual Studio, in one place. Only Visual Studio's own APIs: the tests
// don't reference the extension, so they see it the way a user does.
internal static class Ide
{
    // Must match the extension's command set and its "language server ready" UI context.
    public const string DamlCommandSet = "{9387515d-2cd9-4b38-87ba-e7d2f53a9d0d}";
    public const int RestartLanguageServerCommand = 0x0200;
    private static readonly Guid LanguageServerReady = new("6cc58c93-db1a-42f9-999d-5f010a023df2");

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(Ide).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>
    /// A copy of a sample without build output, so nothing lands in the repository; with
    /// <paramref name="sdkVersion"/>, its manifests are pinned to that SDK instead.
    /// </summary>
    public static string CopySample(string name, string? sdkVersion = null)
    {
        var source = Path.Combine(RepoRoot(), "samples", name);
        var target = Path.Combine(Path.GetTempPath(), "daml-ide-" + Guid.NewGuid().ToString("N").Substring(0, 8), name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            // Build output, and Visual Studio's own per-user state (.vs with its startup item
            // and document layout) which must come from this instance, not from a session that
            // happened to open the sample in the repository.
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith(".", StringComparison.Ordinal)))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
            if (sdkVersion is not null && Path.GetFileName(file) is "daml.yaml" or "multi-package.yaml")
            {
                var manifest = Path.Combine(target, relative);
                File.WriteAllText(manifest, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(manifest), @"(?m)^sdk-version:\s*\S+", "sdk-version: " + sdkVersion));
            }
        }
        return target;
    }

    /// <summary>A copy of a file under <c>test/fixtures</c> in a fresh temp folder, so edits never touch the repository.</summary>
    public static string CopyFixture(string relativePath)
    {
        var source = Path.Combine(RepoRoot(), "test", "fixtures", relativePath);
        var target = Path.Combine(Path.GetTempPath(), "daml-ide-" + Guid.NewGuid().ToString("N").Substring(0, 8), Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        return target;
    }

    /// <summary>Closes every open document without saving, so the next test starts from an empty editor.</summary>
    public static void CloseAllDocuments()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Dte().Documents.CloseAll(vsSaveChanges.vsSaveChangesNo);
    }

    /// <summary>
    /// Kills the damlc processes this Visual Studio started (through dpm), and only those: a
    /// server of another instance, or of a parallel test run, is left alone. Returns how many.
    /// </summary>
    public static int KillLanguageServersOfThisInstance()
    {
        var parents = new Dictionary<int, int>();
        using (var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
        {
            foreach (var row in searcher.Get())
            {
                parents[Convert.ToInt32(row["ProcessId"], System.Globalization.CultureInfo.InvariantCulture)] = Convert.ToInt32(row["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        var self = System.Diagnostics.Process.GetCurrentProcess().Id;
        var killed = 0;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("damlc"))
        {
            using (process)
            {
                if (DescendsFrom(process.Id, self))
                {
                    process.Kill();
                    killed++;
                }
            }
        }
        return killed;

        bool DescendsFrom(int pid, int ancestor)
        {
            for (var hops = 0; hops < 16 && parents.TryGetValue(pid, out var parent); hops++)
            {
                if (parent == ancestor)
                {
                    return true;
                }
                pid = parent;
            }
            return false;
        }
    }

    public static void OpenFolder(string folder)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solution = Service<SVsSolution, IVsSolution7>();
        solution.OpenFolder(folder);
    }

    /// <summary>Closes the folder and waits for its server to stop, so the next test starts clean.</summary>
    public static async Task CloseFolderAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var solution = Service<SVsSolution, IVsSolution>();
        ErrorHandler.ThrowOnFailure(solution.CloseSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_NoSave, null, 0));
        await WaitForServerAsync(ready: false, TimeSpan.FromSeconds(60));
    }

    public static IWpfTextView OpenFile(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out _, out IVsTextView textView);
        return ComponentModel().GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(textView)
            ?? throw new InvalidOperationException($"No editor for {path}.");
    }

    public static IComponentModel ComponentModel() => Service<SComponentModel, IComponentModel>();
    /// <summary>
    /// Go To Definition at the caret of <paramref name="view"/>, sent to the editor itself: a DTE
    /// command goes to whichever window has the focus, which after opening files in code is not
    /// necessarily this one.
    /// </summary>
    public static void GoToDefinition(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var adapter = ComponentModel().GetService<IVsEditorAdaptersFactoryService>().GetViewAdapter(view)
            ?? throw new InvalidOperationException("The view has no adapter.");
        var group = VSConstants.GUID_VSStandardCommandSet97;
        // A failure here is retried by the caller, until damlc has checked the file.
        _ = ((Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget)adapter).Exec(
            ref group, (uint)VSConstants.VSStd97CmdID.GotoDefn, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Collapse to Definitions (Ctrl+M, O) in <paramref name="view"/>, sent to the editor like <see cref="GoToDefinition"/>.</summary>
    public static void CollapseToDefinitions(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var adapter = ComponentModel().GetService<IVsEditorAdaptersFactoryService>().GetViewAdapter(view)
            ?? throw new InvalidOperationException("The view has no adapter.");
        var group = VSConstants.VSStd2K;
        ErrorHandler.ThrowOnFailure(((Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget)adapter).Exec(
            ref group, (uint)VSConstants.VSStd2KCmdID.OUTLN_COLLAPSE_TO_DEF, 0, IntPtr.Zero, IntPtr.Zero));
    }
    // A fresh copy per test, so edits never touch a checked-in file.
    public static (IVsTextView TextView, IWpfTextView View) OpenScratch(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var folder = Path.Combine(Path.GetTempPath(), "daml-vs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Scratch.daml");
        File.WriteAllText(file, text);

        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file, Guid.Empty, out _, out _, out _, out IVsTextView textView);

        var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("SComponentModel is not available.");
        var view = componentModel.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(textView) ?? throw new InvalidOperationException("No WPF view for the scratch file.");
        Assert.Equal("daml", view.TextBuffer.ContentType.TypeName);
        return (textView, view);
    }

    // Types the way the keyboard does, through the editor's command handlers; brace completion
    // lives there and never sees a TYPECHAR sent to the old text view command target.
    public static void TypeChar(IWpfTextView view, char c)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("SComponentModel is not available.");
        var commands = componentModel.GetService<IEditorCommandHandlerServiceFactory>().GetService(view);
        var operations = componentModel.GetService<IEditorOperationsFactoryService>().GetEditorOperations(view);
        // The last handler in the chain is what puts the character into the buffer.
        commands.Execute((textView, buffer) => new TypeCharCommandArgs(textView, buffer, c), () => operations.InsertText(c.ToString()));
    }

    public static DTE Dte() => Service<DTE, DTE>();

    /// <summary>Waits for the extension's "language server ready" UI context to be <paramref name="ready"/>.</summary>
    public static async Task WaitForServerAsync(bool ready, TimeSpan timeout)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var context = UIContext.FromUIContextGuid(LanguageServerReady);
        var deadline = DateTime.UtcNow + timeout;
        while (context.IsActive != ready && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        }
        Assert.True(context.IsActive == ready, (ready ? "The Daml language server never became ready." : "The Daml language server never stopped serving.")
            + Environment.NewLine + "Daml Language Server pane (end):" + Environment.NewLine + PaneText("Daml Language Server", 12000));
    }

    private static readonly string[] LineBreaks = ["\r\n", "\n"];

    /// <summary>The last <paramref name="maxLength"/> characters of an Output pane's log entries, for failure messages.</summary>
    public static string PaneText(string title, int maxLength)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            foreach (OutputWindowPane pane in ((EnvDTE80.DTE2)Dte()).ToolWindows.OutputWindow.OutputWindowPanes)
            {
                if (pane.Name == title)
                {
                    var document = pane.TextDocument;
                    // Stack traces push out the lines that tell what happened; keep the log entries and,
                    // of an error's exception, only the lines that name it: the one right after the
                    // entry and any inner exception.
                    var lines = document.StartPoint.CreateEditPoint().GetText(document.EndPoint).Split(LineBreaks, StringSplitOptions.None);
                    var kept = new List<string>();
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var afterError = i > 0 && lines[i - 1].StartsWith("[", StringComparison.Ordinal) && lines[i - 1].Contains(" fail ");
                        if (lines[i].StartsWith("[", StringComparison.Ordinal) || afterError || lines[i].Contains(" ---> "))
                        {
                            kept.Add(lines[i]);
                        }
                    }
                    var text = string.Join(Environment.NewLine, kept);
                    return text.Length <= maxLength ? text : text.Substring(text.Length - maxLength);
                }
            }
            return $"(no '{title}' pane)";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return $"(couldn't read the '{title}' pane: {ex.Message})";
        }
    }

    public static void RunDamlCommand(int id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        object? input = null;
        object? output = null;
        Dte().Commands.Raise(DamlCommandSet, id, ref input, ref output);
    }

    /// <summary>Runs dpm like a user would, for set-up steps such as building a sample.</summary>
    public static async Task DpmAsync(string workingDirectory, params string[] arguments)
    {
        var dpm = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dpm", "bin", "dpm.cmd"),
            }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, "dpm.cmd")))
            .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("dpm is not installed.");
        var start = new ProcessStartInfo("cmd.exe", "/d /s /c \"\"" + dpm + "\" " + string.Join(" ", arguments) + "\"")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Visual Studio has no console; without its own stdin, dpm can't start damlc.
            RedirectStandardInput = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        var streams = Task.WhenAll(output, errors);
        Assert.True(await Task.WhenAny(streams, Task.Delay(TimeSpan.FromMinutes(5))) == streams, "dpm " + string.Join(" ", arguments) + " didn't finish.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dpm {string.Join(" ", arguments)} failed: {await output}{await errors}");
    }

    private static TInterface Service<TService, TInterface>()
        where TInterface : class =>
        ServiceProvider.GlobalProvider.GetService(typeof(TService)) as TInterface
            ?? throw new InvalidOperationException($"{typeof(TService).Name} is not available.");
}
