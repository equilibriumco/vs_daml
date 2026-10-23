using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

// What the IDE tests do to Visual Studio, in one place. Visual Studio's own APIs, so the tests see
// the extension the way a user does; only setting an option reaches into it (SetDamlOptionAsync).
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
            PinSdk(Path.Combine(target, relative), sdkVersion);
        }
        return target;
    }

    // Puts another SDK version into a copied manifest; other files, and a null version, are left as they are.
    private static void PinSdk(string file, string? sdkVersion)
    {
        if (sdkVersion is not null && Path.GetFileName(file) is "daml.yaml" or "multi-package.yaml")
        {
            File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"(?m)^sdk-version:\s*\S+", "sdk-version: " + sdkVersion));
        }
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

    // The editor's brace completion pairs from its built-in set, apostrophe included, until the
    // Daml language configuration has loaded: once per session, in the background, after the
    // first Daml document opens. An apostrophe typed at the end of the buffer tells which set is
    // in force, so this types one until the configured set answers.
    public static async Task WaitForLanguageConfigurationAsync(IWpfTextView view)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var end = view.TextBuffer.CurrentSnapshot.Length;
            view.Caret.MoveTo(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, end));
            TypeChar(view, '\'');
            var inserted = view.TextBuffer.CurrentSnapshot.Length - end;
            view.TextBuffer.Delete(new Span(end, inserted));
            if (inserted == 1)
            {
                return;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Brace completion did not take the Daml language configuration in time.");
            }
            await Task.Delay(200);
        }
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
            + Environment.NewLine + "EQ Daml Tools: Language Server pane (end):" + Environment.NewLine + PaneText("EQ Daml Tools: Language Server", 12000));
    }

    /// <summary>
    /// Checks <paramref name="condition"/> on the UI thread every so often until it holds or
    /// <paramref name="within"/> has passed; whether it held.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await condition())
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(200);
        }
    }

    /// <summary>Whether the active document becomes <paramref name="path"/> within <paramref name="within"/>.</summary>
    public static Task<bool> OpensAsync(string path, TimeSpan within) =>
        UntilAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return string.Equals(Dte().ActiveDocument?.FullName, path, StringComparison.OrdinalIgnoreCase);
        }, within);

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

    /// <summary>
    /// A new solution from a dotnet new template of this build (artifacts\packages, which
    /// scripts/pack.ps1 makes), in a fresh temp folder. Its nuget.config gives it a package folder
    /// of its own, so it restores this build's packages and never an older copy of the same version
    /// from the machine's cache; everything else comes from that cache.
    /// </summary>
    public static async Task<string> NewSolutionFromTemplateAsync(string template, string name)
    {
        var packages = Path.Combine(RepoRoot(), "artifacts", "packages");
        var templates = Directory.Exists(packages) ? Directory.GetFiles(packages, "*.Templates.*.nupkg").FirstOrDefault() : null;
        Assert.True(templates is not null, $"No templates package in {packages}; run pwsh scripts/pack.ps1 before the IDE tests.");
        var root = Path.Combine(Path.GetTempPath(), "daml-ide-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        UseThisBuildsPackages(root);
        var hive = Path.Combine(root, "hive");
        await RunAsync(root, "dotnet", "new", "install", templates!, "--debug:custom-hive", hive);
        await RunAsync(root, "dotnet", "new", template, "-n", name, "-o", Path.Combine(root, name), "--debug:custom-hive", hive);
        return Path.Combine(root, name);
    }

    /// <summary>
    /// A copy of a folder under <c>test/fixtures</c> in a fresh temp folder, without what a build or
    /// Visual Studio left in it there and without its own nuget.config, restoring this build's packages.
    /// A <paramref name="sdkVersion"/> replaces the SDK its Daml manifests pin.
    /// </summary>
    public static string CopyFixtureFolder(string relativePath, string? sdkVersion = null)
    {
        var source = Path.Combine(RepoRoot(), "test", "fixtures", relativePath);
        var root = Path.Combine(Path.GetTempPath(), "daml-ide-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => p.StartsWith(".", StringComparison.Ordinal) || p is "obj" or "bin") || relative.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!);
            File.Copy(file, Path.Combine(root, relative));
            PinSdk(Path.Combine(root, relative), sdkVersion);
        }
        UseThisBuildsPackages(root);
        return root;
    }

    /// <summary>
    /// The scale fixture (scripts/new-scale-fixture.ps1, its default sizes) on this build's packages,
    /// in a fresh temp folder; returns the folder with Scale.slnx.
    /// </summary>
    public static async Task<string> NewScaleFixtureAsync()
    {
        var packages = Path.Combine(RepoRoot(), "artifacts", "packages");
        var sdk = Directory.Exists(packages) ? Directory.GetFiles(packages, "*.Sdk.*.nupkg").Select(Path.GetFileName).FirstOrDefault() : null;
        var name = sdk is null ? null : System.Text.RegularExpressions.Regex.Match(sdk, @"^(?<prefix>.+)\.Sdk\.(?<version>\d.+)\.nupkg$");
        Assert.True(name is { Success: true }, $"No Equilibrium.Daml.Sdk package in {packages}; run pwsh scripts/pack.ps1 before the IDE tests.");
        var app = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), @"src\Equilibrium.Daml.Templates\content\daml-dotnet-app\DamlApp\DamlApp.csproj"));
        var runtime = (string)app.Descendants("PackageReference").First(r => (string?)r.Attribute("Include") == "Daml.Runtime").Attribute("Version")!;
        var root = Path.Combine(Path.GetTempPath(), "daml-ide-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        UseThisBuildsPackages(root);
        var fixture = Path.Combine(root, "Scale");
        await RunAsync(root, "pwsh", "-NoProfile", "-File", Path.Combine(RepoRoot(), @"scripts\new-scale-fixture.ps1"), "-Path", fixture,
            "-SdkPackageVersion", name!.Groups["version"].Value, "-RuntimeVersion", runtime, "-PackagePrefix", name.Groups["prefix"].Value);
        return fixture;
    }

    // A package folder of its own, so the projects under root restore this build's packages (from
    // artifacts\packages, which scripts/pack.ps1 makes) and never an older copy of the same version
    // from the machine's cache; everything else comes from that cache. The repository's SDK pin too.
    private static void UseThisBuildsPackages(string root)
    {
        var packages = Path.Combine(RepoRoot(), "artifacts", "packages");
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(RepoRoot(), "global.json"), Path.Combine(root, "global.json"), overwrite: true);
        var machineCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        File.WriteAllText(Path.Combine(root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
              <packageSources>
                <clear />
                <add key="this-build" value="{packages}" />
                <add key="machine-cache" value="{machineCache}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="this-build">
                  <package pattern="*.Sdk" />
                  <package pattern="*.Build.CSharp" />
                </packageSource>
                <packageSource key="machine-cache">
                  <package pattern="*" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
    }

    /// <summary>Opens a solution and waits until every project in it has loaded.</summary>
    public static async Task OpenSolutionAsync(string path)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Dte().Solution.Open(path);
        var loaded = UIContext.FromUIContextGuid(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_guid);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (!loaded.IsActive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        }
        Assert.True(loaded.IsActive, $"{path} didn't finish loading.");
    }

    /// <summary>Closes the solution without saving and waits for its language server to stop.</summary>
    public static async Task CloseSolutionAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Dte().Solution.Close(false);
        await WaitForServerAsync(ready: false, TimeSpan.FromSeconds(60));
    }

    /// <summary>The projects of the open solution: their names and whether Visual Studio could load them.</summary>
    public static IReadOnlyList<(string Name, bool Loaded)> SolutionProjects()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var projects = new List<(string, bool)>();
        foreach (Project project in Dte().Solution.Projects)
        {
            // A project Visual Studio couldn't load is an "unmodeled" placeholder.
            projects.Add((project.Name, !string.Equals(project.Kind, EnvDTE.Constants.vsProjectKindUnmodeled, StringComparison.OrdinalIgnoreCase)));
        }
        return projects;
    }

    /// <summary>Build > Build Solution; returns how many projects failed.</summary>
    public static async Task<int> BuildSolutionAsync(TimeSpan timeout)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Dte();
        var build = dte.Solution.SolutionBuild;
        // Visual Studio's own event, not the build state: a state read right after the call may
        // still be the previous build's "done", and a quick build may come and go between reads.
        // The events object is kept in a local, or it and its handler can be collected.
        var events = dte.Events.BuildEvents;
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispBuildEvents_OnBuildDoneEventHandler done = (_, _) => finished.TrySetResult(true);
        events.OnBuildDone += done;
        try
        {
            build.Build(WaitForBuildToFinish: false);
            var first = await Task.WhenAny(finished.Task, Task.Delay(timeout));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Assert.True(first == finished.Task, "Build Solution didn't finish." + Environment.NewLine + BuildOutput());
        }
        finally
        {
            events.OnBuildDone -= done;
        }
        GC.KeepAlive(events);
        return build.LastBuildInfo;
    }

    /// <summary>The Build pane of the Output window, found by its id: its title depends on Visual Studio's language.</summary>
    public static string BuildOutput()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var id = VSConstants.OutputWindowPaneGuid.BuildOutputPane_guid.ToString("B");
        foreach (OutputWindowPane pane in ((EnvDTE80.DTE2)Dte()).ToolWindows.OutputWindow.OutputWindowPanes)
        {
            if (string.Equals(pane.Guid, id, StringComparison.OrdinalIgnoreCase))
            {
                var document = pane.TextDocument;
                return document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
            }
        }
        return "(no Build pane)";
    }

    /// <summary>
    /// Runs a command of the extension in <paramref name="view"/>, sent to the editor like
    /// <see cref="GoToDefinition"/>; false when the editor didn't take it (not available there yet).
    /// </summary>
    public static bool ExecuteDamlCommand(IWpfTextView view, int id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var adapter = ComponentModel().GetService<IVsEditorAdaptersFactoryService>().GetViewAdapter(view)
            ?? throw new InvalidOperationException("The view has no adapter.");
        var group = new Guid(DamlCommandSet);
        return ErrorHandler.Succeeded(((Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget)adapter).Exec(ref group, (uint)id, 0, IntPtr.Zero, IntPtr.Zero));
    }

    /// <summary>The active document and the text of the line its caret is on.</summary>
    public static (string Path, string Line) ActiveDocumentAndLine()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var document = Dte().ActiveDocument;
        if (document?.Selection is not TextSelection selection)
        {
            return ("", "");
        }
        var start = selection.ActivePoint.CreateEditPoint();
        start.StartOfLine();
        return (document.FullName, start.GetText(selection.ActivePoint.LineLength));
    }

    /// <summary>Waits for a tool window whose caption <paramref name="caption"/> accepts, and returns that caption, or null.</summary>
    public static async Task<string?> WaitForToolWindowAsync(Func<string, bool> caption, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var shell = Service<SVsUIShell, IVsUIShell>();
            ErrorHandler.ThrowOnFailure(shell.GetToolWindowEnum(out var frames));
            var frame = new IVsWindowFrame[1];
            while (frames.Next(1, frame, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                if (ErrorHandler.Succeeded(frame[0].GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var title)) && title is string text && caption(text))
                {
                    return text;
                }
            }
            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }
            await Task.Delay(500);
        }
    }

    /// <summary>The rows the sources of a table manager hold now, as the values of the given columns.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> TableRows(string manager, params string[] columns)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var sink = new CollectingSink();
        foreach (var source in ComponentModel().GetService<ITableManagerProvider>().GetTableManager(manager).Sources)
        {
            using (source.Subscribe(sink))
            {
            }
        }
        foreach (var entry in sink.Entries)
        {
            rows.Add(columns.ToDictionary(c => c, c => entry.TryGetValue(c, out var value) ? value : null));
        }
        return rows;
    }

    // Only what a source hands over at once when subscribed to.
    private sealed class CollectingSink : ITableDataSink
    {
        public List<ITableEntry> Entries { get; } = new();

        public bool IsStable { get; set; }

        public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries = false)
        {
            if (removeAllEntries)
            {
                Entries.Clear();
            }
            Entries.AddRange(newEntries);
        }

        public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries) => Entries.RemoveAll(oldEntries.Contains);

        public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
        {
            RemoveEntries(oldEntries);
            Entries.AddRange(newEntries);
        }

        public void RemoveAllEntries() => Entries.Clear();

        public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots = false) => throw new NotSupportedException();

        public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot) => throw new NotSupportedException();

        public void RemoveAllSnapshots() => throw new NotSupportedException();

        public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot) => throw new NotSupportedException();

        public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories = false) => throw new NotSupportedException();

        public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory) => throw new NotSupportedException();

        public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory) => throw new NotSupportedException();

        public void FactorySnapshotChanged(ITableEntriesSnapshotFactory? factory) => throw new NotSupportedException();

        public void RemoveAllFactories() => throw new NotSupportedException();
    }

    /// <summary>The errors and warnings in the Error List, one per line, for failure messages.</summary>
    public static string ErrorListText()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var items = ((EnvDTE80.DTE2)Dte()).ToolWindows.ErrorList.ErrorItems;
        var lines = new List<string>();
        for (var i = 1; i <= items.Count; i++)
        {
            var item = items.Item(i);
            lines.Add($"{item.ErrorLevel} {item.Project} {item.FileName}({item.Line}): {item.Description}");
        }
        return lines.Count == 0 ? "(the Error List is empty)" : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The errors the Error List has for <paramref name="file"/>, read from its table once it caught
    /// up with its sources, the language server among them.
    /// </summary>
    public static async Task<int> ErrorsInAsync(string file)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var errorList = Service<SVsErrorList, IErrorList>();
        await errorList.TableControl.ForceUpdateAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return errorList.TableControl.Entries.Count(e =>
            e.TryGetValue(StandardTableKeyNames.DocumentName, out var document) && document is string path
            && string.Equals(path, file, StringComparison.OrdinalIgnoreCase)
            && e.TryGetValue(StandardTableKeyNames.ErrorSeverity, out var severity) && Equals(severity, __VSERRORCATEGORY.EC_ERROR));
    }

    /// <summary>
    /// Sets an option of Tools > Options > EQ Daml Tools > General; <paramref name="name"/> is the
    /// option's property. Returns the value it had, to put back afterwards.
    /// </summary>
    // The one place the tests reach into the extension, by reflection: a value set through
    // DTE.Properties only changes the page's object in memory and is never seen by the product,
    // so this goes through the extension's own options model and saves to the settings store.
    public static async Task<object?> SetDamlOptionAsync(string name, object value)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Equilibrium.Daml.VisualStudio");
        Assert.True(assembly is not null, "The Daml extension isn't loaded yet; open a Daml file first.");
        var options = assembly!.GetType("Equilibrium.Daml.VisualStudio.Options.DamlOptions")!;
        var liveTask = (Task)options.GetMethod("GetLiveInstanceAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)!.Invoke(null, null)!;
        await liveTask;
        var live = liveTask.GetType().GetProperty("Result")!.GetValue(liveTask)!;
        var property = options.GetProperty(name) ?? throw new ArgumentException($"DamlOptions has no option {name}.", nameof(name));
        var before = property.GetValue(live);
        property.SetValue(live, value);
        await (Task)options.GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)!.Invoke(live, null)!;
        return before;
    }

    /// <summary>What Build Solution left behind, for failure messages.</summary>
    public static string BuildReport()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return BuildOutput() + Environment.NewLine + ErrorListText();
    }

    /// <summary>Runs a program to the end and returns what it printed; a non-zero exit code fails the test.</summary>
    public static async Task<string> RunAsync(string workingDirectory, string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName, string.Join(" ", arguments.Select(a => a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a)))
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        var streams = Task.WhenAll(output, errors);
        var command = fileName + " " + string.Join(" ", arguments);
        Assert.True(await Task.WhenAny(streams, Task.Delay(TimeSpan.FromMinutes(2))) == streams, command + " didn't finish.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{command} failed with exit code {process.ExitCode}: {await output}{await errors}");
        return await output;
    }

    private static TInterface Service<TService, TInterface>()
        where TInterface : class =>
        ServiceProvider.GlobalProvider.GetService(typeof(TService)) as TInterface
            ?? throw new InvalidOperationException($"{typeof(TService).Name} is not available.");
}
