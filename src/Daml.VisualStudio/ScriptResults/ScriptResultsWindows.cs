using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.ScriptResults;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.VirtualResources;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Task = System.Threading.Tasks.Task;

namespace Daml.VisualStudio.ScriptResults;

/// <summary>
/// The script results windows, one per results resource. Only <see cref="ShowAsync"/> brings a
/// window up, and only because the user asked; new results and notes go into a window without
/// activating it, so typing in the editor never loses focus to it.
/// </summary>
[Export(typeof(ScriptResultsWindows))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class ScriptResultsWindows : IVirtualResourceWindows, IDisposable
{
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;
    private readonly IAsyncServiceProvider _services;
    // Two spellings of one results address are one window.
    private readonly ScriptResultsWindowIds _ids = new(VirtualResourceAddressComparer.Instance);
    private readonly Dictionary<string, ScriptResultsToolWindow> _open = new(VirtualResourceAddressComparer.Instance);
    private readonly ViewStateStore _views;
    private AsyncLazy<CoreWebView2Environment>? _environment;

    [ImportingConstructor]
    public ScriptResultsWindows(JoinableTaskContext joinableTaskContext, IDamlLogger logger, [Import(typeof(SAsyncServiceProvider))] IAsyncServiceProvider services)
    {
        _joinableTaskFactory = joinableTaskContext.Factory;
        _logger = logger;
        _services = services;
        _views = new ViewStateStore(logger);
    }

    /// <summary>Where the extension keeps its browser profile and the windows' page folders.</summary>
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamlVisualStudio");

    /// <summary>The page script, stylesheet and shim shipped with the extension.</summary>
    public static string AssetsFolder { get; } =
        Path.Combine(Path.GetDirectoryName(typeof(ScriptResultsWindows).Assembly.Location)!, "Assets", "webview");

    /// <summary>A results window was closed by the user; its resource is the argument.</summary>
    public event EventHandler<string>? Closed;

    /// <summary>True while <paramref name="resource"/> has a window.</summary>
    public bool IsOpen(string resource)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _open.ContainsKey(resource);
    }

    /// <summary>Opens the window for <paramref name="resource"/>, or brings it up, and activates it.</summary>
    public async Task ShowAsync(string resource, string title, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var window = await GetOrCreateAsync(resource, cancellationToken);
        window.Caption = title;
        if (window.Frame is IVsWindowFrame frame)
        {
            ErrorHandler.ThrowOnFailure(frame.Show());
        }
    }

    /// <summary>New results for an open window; ignored when <paramref name="resource"/> has none.</summary>
    public async Task UpdateAsync(string resource, string html)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        if (_open.TryGetValue(resource, out var window) && window.View is { } view)
        {
            await view.UpdateAsync(html);
        }
    }

    /// <summary>A note for an open window; ignored when <paramref name="resource"/> has none.</summary>
    public async Task AddNoteAsync(string resource, string text)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        if (_open.TryGetValue(resource, out var window) && window.View is { } view)
        {
            await view.AddNoteAsync(text);
        }
    }

    /// <summary>Stops saving views; Visual Studio is shutting down.</summary>
    public void Dispose() => _views.Dispose();

    private async Task<ScriptResultsToolWindow> GetOrCreateAsync(string resource, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (_open.TryGetValue(resource, out var existing))
        {
            return existing;
        }

        var package = await LoadPackageAsync(cancellationToken);
        var id = _ids.GetOrAdd(resource);
        var window = (ScriptResultsToolWindow)await package.FindToolWindowAsync(typeof(ScriptResultsToolWindow), id, create: true, cancellationToken);
        _open[resource] = window;
        var workspace = await WorkspaceFolderAsync();
        var state = await _views.GetAsync(workspace, resource);
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var view = new ScriptResultsView(resource, state, GetEnvironmentAsync, changed => _views.Set(resource, changed), _logger, _joinableTaskFactory, SourcesFor(workspace).Contains);
        window.Attach(view);
        if (window.Frame is IVsWindowFrame frame)
        {
            ErrorHandler.ThrowOnFailure(frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, new FrameEvents(() => OnClosed(resource, id, frame))));
        }
        _joinableTaskFactory.RunAsync(() => view.InitializeAsync(CancellationToken.None)).FileAndForget("daml/scriptResults/initialize");
        return window;
    }

    // Closing a tool window only hides it; ours are destroyed instead, so the browser and its
    // processes go with them and the next show starts afresh.
    private void OnClosed(string resource, int id, IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_open.Remove(resource))
        {
            return;
        }
        _ids.Remove(id);
        Closed?.Invoke(this, resource);
        _joinableTaskFactory.RunAsync(async () =>
        {
            // Not from inside the frame's own notification.
            await Task.Yield();
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }).FileAndForget("daml/scriptResults/close");
    }

    private async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        // One profile shared by every window of this Visual Studio.
        _environment ??= new AsyncLazy<CoreWebView2Environment>(
            () => CoreWebView2Environment.CreateAsync(null, Path.Combine(DataFolder, "WebView2")),
            _joinableTaskFactory);
        return await _environment.GetValueAsync();
    }

    private async Task<AsyncPackage> LoadPackageAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var shell = await _services.GetServiceAsync(typeof(SVsShell)) as IVsShell7
            ?? throw new InvalidOperationException("The shell service is not available.");
        var guid = new Guid(PackageGuids.PackageString);
        return await shell.LoadPackageAsync(ref guid) as AsyncPackage
            ?? throw new InvalidOperationException("The Daml package didn't load.");
    }

    // The open folder or the solution's folder; null when nothing is open.
    private async Task<string?> WorkspaceFolderAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        if (await _services.GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution
            && ErrorHandler.Succeeded(solution.GetSolutionInfo(out var directory, out _, out _))
            && !string.IsNullOrEmpty(directory))
        {
            return directory;
        }
        return null;
    }

    // Links in the results may open the workspace's own files and the short copies of library
    // sources that Go To Definition uses; nothing else on the disk.
    private static AllowedSources SourcesFor(string? workspace)
    {
        var libraryCopies = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Daml", "VisualStudio", "library");
        return new AllowedSources(workspace is null ? [libraryCopies] : [workspace, libraryCopies]);
    }
    private sealed class FrameEvents(Action closed) : IVsWindowFrameNotify3
    {
        public int OnShow(int fShow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinClosed)
            {
                closed();
            }
            return VSConstants.S_OK;
        }

        public int OnMove(int x, int y, int w, int h) => VSConstants.S_OK;

        public int OnSize(int x, int y, int w, int h) => VSConstants.S_OK;

        public int OnDockableChange(int fDockable, int x, int y, int w, int h) => VSConstants.S_OK;

        public int OnClose(ref uint pgrfSaveOptions) => VSConstants.S_OK;
    }
}
