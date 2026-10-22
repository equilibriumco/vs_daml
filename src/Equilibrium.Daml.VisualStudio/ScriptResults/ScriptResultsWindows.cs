using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Equilibrium.Daml.VisualStudio.Core.Server;
using Equilibrium.Daml.VisualStudio.LanguageServer.VirtualResources;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.ScriptResults;

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
    // How many times the windows were closed with their folder; a window being made when that
    // happens is abandoned, since it belongs to the closed folder too.
    private int _closings;

    [ImportingConstructor]
    public ScriptResultsWindows(JoinableTaskContext joinableTaskContext, IDamlLogger logger, [Import(typeof(SAsyncServiceProvider))] IAsyncServiceProvider services)
    {
        _joinableTaskFactory = joinableTaskContext.Factory;
        _logger = logger;
        _services = services;
        _views = new ViewStateStore(logger);
    }

    /// <summary>Where the extension keeps its browser profile and the windows' page folders.</summary>
    public static string DataFolder => ExtensionFolders.Data;

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
        var window = await GetOrCreateAsync(resource, title, cancellationToken);
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

    /// <summary>
    /// Closes every results window as if the user had: their folder or solution has closed, so
    /// their server is gone, their source links lead nowhere, and the next folder may have
    /// scripts of the same names. The views chosen in them stay saved with the folder.
    /// </summary>
    public async Task CloseAllAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        _closings++;
        if (_open.Count > 0)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Closing {_open.Count} script results window(s) of the closed folder.");
        }
        foreach (var resource in _open.Keys.ToList())
        {
            // Closed the way the user closes it: that hides the window and reports it, and the
            // report destroys it. One that reports nothing is let go here the same way (an open
            // window's resource has its id already; GetOrAdd only looks it up).
            var frame = _open[resource].Frame as IVsWindowFrame;
            frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            if (_open.ContainsKey(resource))
            {
                OnClosed(resource, _ids.GetOrAdd(resource), frame);
            }
        }
    }

    /// <summary>Stops saving views; Visual Studio is shutting down.</summary>
    public void Dispose() => _views.Dispose();

    // A window counts as open only once it is complete: titled, with its view and its close
    // handler. If any step fails, the half-made frame is closed and its id freed, so the next
    // show starts over instead of bringing up an empty window that can never fill.
    private async Task<ScriptResultsToolWindow> GetOrCreateAsync(string resource, string title, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (_open.TryGetValue(resource, out var existing))
        {
            return existing;
        }

        var closings = _closings;
        var package = await LoadPackageAsync(cancellationToken);
        var id = _ids.GetOrAdd(resource);
        ScriptResultsToolWindow? window = null;
        try
        {
            window = (ScriptResultsToolWindow)await package.FindToolWindowAsync(typeof(ScriptResultsToolWindow), id, create: true, cancellationToken);
            window.Caption = title;
            var workspace = await WorkspaceFolderAsync();
            var state = await _views.GetAsync(workspace, resource);
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (_open.TryGetValue(resource, out existing))
            {
                return existing; // another show of the same results got there first
            }
            if (closings != _closings)
            {
                throw new OperationCanceledException("The folder closed while its script results window was opening.");
            }

            var view = new ScriptResultsView(resource, state, GetEnvironmentAsync, changed => _views.Set(resource, changed), _logger, _joinableTaskFactory, SourcesFor(workspace).Contains);
            window.Attach(view);
            if (window.Frame is IVsWindowFrame frame)
            {
                ErrorHandler.ThrowOnFailure(frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, new FrameEvents(() => OnClosed(resource, id, frame))));
            }
            _open[resource] = window;
            _joinableTaskFactory.RunAsync(() => view.InitializeAsync(CancellationToken.None)).FileAndForget("daml/scriptResults/initialize");
            return window;
        }
        catch
        {
            // Registering is the last step, so a window that got here was never registered. The
            // clean-up runs even when the show was cancelled.
            await _joinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
            _ids.Remove(id);
            if (window?.Frame is IVsWindowFrame orphan)
            {
                orphan.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            }
            throw;
        }
    }

    // Closing a tool window only hides it; ours are destroyed instead, so the browser and its
    // processes go with them and the next show starts afresh. Destroying the frame notifies
    // again, which finds the resource already gone.
    private void OnClosed(string resource, int id, IVsWindowFrame? frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_open.Remove(resource))
        {
            return;
        }
        _ids.Remove(id);
        Closed?.Invoke(this, resource);
        if (frame is null)
        {
            return;
        }
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
        var libraryCopies = ExtensionFolders.LibraryCopies;
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
