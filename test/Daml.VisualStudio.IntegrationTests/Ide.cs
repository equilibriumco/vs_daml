using System;
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

    /// <summary>A copy of a sample without build output, so nothing lands in the repository.</summary>
    public static string CopySample(string name)
    {
        var source = Path.Combine(RepoRoot(), "samples", name);
        var target = Path.Combine(Path.GetTempPath(), "daml-ide-" + Guid.NewGuid().ToString("N").Substring(0, 8), name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            if (relative.Split(Path.DirectorySeparatorChar).Contains(".daml"))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }
        return target;
    }

    public static void OpenFolder(string folder)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solution = Service<SVsSolution, IVsSolution7>();
        solution.OpenFolder(folder);
    }

    public static void CloseFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solution = Service<SVsSolution, IVsSolution>();
        ErrorHandler.ThrowOnFailure(solution.CloseSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_NoSave, null, 0));
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
        Assert.True(context.IsActive == ready, ready ? "The Daml language server never became ready." : "The Daml language server never stopped serving.");
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
