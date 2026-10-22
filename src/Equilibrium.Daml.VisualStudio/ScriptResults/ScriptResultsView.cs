using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.ScriptResults;

/// <summary>
/// The browser half of a results window. The page and its assets are served from a folder of
/// this window's own under one virtual origin; the page's view (table or transaction, the two
/// checkboxes) is kept here and put back after every reload, since new results and theme
/// changes both reload the page. Nothing here ever shows or activates the window.
/// </summary>
internal sealed class ScriptResultsView : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    // The browser normally starts in a second or two; a runtime that hangs mustn't leave the
    // window saying "Loading" forever.
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(30);

    private readonly string _resource;
    private readonly Func<Task<CoreWebView2Environment>> _environment;
    private readonly Action<ScriptResultsViewState> _viewChanged;
    private readonly IDamlLogger _logger;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly ContentControl _host = new();
    private readonly ScriptResultsUpdates _updates = new(Quiet, TimeProvider.System);
    private readonly Debouncer _themeChanges = new(Quiet, TimeProvider.System);
    private readonly string _folder;
    private readonly object _pageGate = new();
    private readonly Func<string, bool> _isAllowedSource;
    private string? _shownResults;
    private WebView2? _browser;
    private CoreWebView2Environment? _browserEnvironment;
    private string? _currentPage;
    private ScriptResultsViewState _view;
    private ScriptResultsTheme? _theme;
    private int _pageVersion;
    private bool _disposed;

    public ScriptResultsView(
        string resource,
        ScriptResultsViewState view,
        Func<Task<CoreWebView2Environment>> environment,
        Action<ScriptResultsViewState> viewChanged,
        IDamlLogger logger,
        JoinableTaskFactory joinableTaskFactory,
        Func<string, bool>? isAllowedSource = null)
    {
        _resource = resource;
        // Without a say on which sources may open, none do.
        _isAllowedSource = isAllowedSource ?? (_ => false);
        _view = view;
        _environment = environment;
        _viewChanged = viewChanged;
        _logger = logger;
        _joinableTaskFactory = joinableTaskFactory;
        _folder = Path.Combine(ScriptResultsWindows.DataFolder, "webview", Guid.NewGuid().ToString("N"));
        _host.Content = Message("Loading script results…");
    }

    public FrameworkElement Element => _host;

    /// <summary>
    /// Starts the browser and shows a waiting page. A missing or outdated runtime, or one that
    /// doesn't start in time, is reported in the window instead.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var theme = CurrentTheme();
        await TaskScheduler.Default;
        PrepareFolder(theme);
        var runtime = WebViewRuntime.Check(InstalledRuntime());

        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (_disposed)
        {
            return;
        }
        if (runtime.Status != RuntimeStatus.Ready)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, $"Script results can't be shown for {_resource}: {runtime.Message}");
            _host.Content = Message(runtime.Message);
            return;
        }

        var browser = new WebView2();
        _host.Content = browser;
        var start = StartAsync(browser);
        using (var limit = new CancellationTokenSource())
        {
            var first = await Task.WhenAny(start, Task.Delay(StartLimit, limit.Token));
            limit.Cancel();
            if (first != start)
            {
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, $"Script results: the browser control didn't start within {StartLimit.TotalSeconds:0} s for {_resource}.");
                _host.Content = Message(WebViewRuntime.TimedOut(StartLimit));
                DisposeWhenStarted(start, browser);
                return;
            }
        }

        CoreWebView2Environment environment;
        try
        {
            environment = await start;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or FileNotFoundException or COMException or InvalidOperationException
            or NotImplementedException or InvalidCastException)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, $"Script results can't be shown: the browser control didn't start for {_resource}.", ex);
            _host.Content = Message(WebViewRuntime.Failed(ex.Message));
            browser.Dispose();
            return;
        }
        if (_disposed)
        {
            browser.Dispose();
            return;
        }
        _browser = browser;
        _browserEnvironment = environment;
        Configure(browser.CoreWebView2);
        _theme = theme;
        VSColorTheme.ThemeChanged += OnThemeChanged;
        await LoadAsync(ScriptResultsPage.Message("Waiting for results…"));
    }

    /// <summary>
    /// New results. Shown once a burst of them is over and only if they changed; results the
    /// shipped page script can't drive are replaced by a message saying so.
    /// </summary>
    public async Task UpdateAsync(string html)
    {
        var shown = await _updates.SubmitAsync(html).ConfigureAwait(false);
        if (shown is null)
        {
            return;
        }
        var missing = ResultsPageFormat.MissingParts(shown);
        if (missing.Count > 0)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Script results for {_resource} are in a format this extension doesn't know (missing: {string.Join(", ", missing)}).");
            await LoadAsync(ScriptResultsPage.Unsupported(missing));
            return;
        }
        Volatile.Write(ref _shownResults, shown);
        await LoadAsync(ScriptResultsPage.Compose(shown));
    }

    /// <summary>A note from the server above the results, e.g. how long the script has been running.</summary>
    public async Task AddNoteAsync(string text)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        if (!_disposed && _browser?.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson(PageMessages.AddNote(text));
            // Only a new page removes the note, so the results shown before it must load again.
            _updates.Forget();
        }
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        VSColorTheme.ThemeChanged -= OnThemeChanged;
        _updates.Dispose();
        _themeChanges.Dispose();
        if (_browser is { } browser)
        {
            if (browser.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnWebMessage;
                core.WebResourceRequested -= OnWebResourceRequested;
                core.FrameNavigationStarting -= OnFrameNavigationStarting;
                core.PermissionRequested -= OnPermissionRequested;
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            }
            browser.Dispose();
            _browser = null;
        }
        _host.Content = null;
        var folder = _folder;
        // The browser lets go of the files shortly after it is disposed; nobody waits for this.
        _joinableTaskFactory.RunAsync(async () =>
        {
            await TaskScheduler.Default;
            DeleteFolder(folder);
        }).FileAndForget("daml/scriptResults/deleteFolder");
    }

    private void Configure(CoreWebView2 core)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.SetVirtualHostNameToFolderMapping(ScriptResultsPage.HostName, _folder, CoreWebView2HostResourceAccessKind.Deny);
        // Every request passes the allowlist, the page's own files included: nothing else
        // from the folder and nothing from the network.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
    }

    private async Task LoadAsync(string page)
    {
        var version = Interlocked.Increment(ref _pageVersion);
        await TaskScheduler.Default;
        // Two loads write on pool threads in no particular order; under the lock, a load that
        // has been overtaken doesn't write at all, so the file always holds the newest page.
        lock (_pageGate)
        {
            if (version != Volatile.Read(ref _pageVersion))
            {
                return;
            }
            File.WriteAllText(Path.Combine(_folder, ScriptResultsPage.PageName), page, new UTF8Encoding(false));
        }
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        // A fresh query each time, so the browser never serves an earlier page from its cache.
        if (_disposed || version != Volatile.Read(ref _pageVersion) || _browser?.CoreWebView2 is not { } core)
        {
            return;
        }
        _currentPage = ScriptResultsPage.Origin + ScriptResultsPage.PageName + "?v=" + version;
        core.Navigate(_currentPage);
    }

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        var message = PageMessages.Parse(e.Source, e.WebMessageAsJson, _currentPage);
        if (message.Rejection is { } rejection)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Script results page: refused {rejection}");
            return;
        }
        _view = _view.Apply(message.Command!, message.Value!);
        _viewChanged(_view);
    }

    // Only the results page loads in the window. A clicked source link opens the source and a
    // clicked web link the browser; everything else is stopped.
    private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var decision = ResultsNavigation.Classify(e.Uri, e.IsUserInitiated);
        if (decision.Kind == NavigationKind.OwnPage)
        {
            return;
        }
        e.Cancel = true;
        Follow(decision);
    }

    // A link may also reach the window as a request for a new window (a middle click) or for an
    // app that handles its scheme (`command:` is one Windows doesn't know); either way it goes
    // where a plain click would, and nothing opens outside the window.
    private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Follow(ResultsNavigation.Classify(e.Uri, e.IsUserInitiated));
    }

    private void OnLaunchingExternalUriScheme(object sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        Follow(ResultsNavigation.Classify(e.Uri, e.IsUserInitiated));
    }

    private void Follow(NavigationDecision decision)
    {
        switch (decision.Kind)
        {
            case NavigationKind.OwnPage:
                break; // the page itself, asked for in a new window: it is already here
            case NavigationKind.RevealLocation when _isAllowedSource(decision.Reveal!.File):
                _joinableTaskFactory.RunAsync(() => SourceReveal.RevealAsync(decision.Reveal, _logger, _joinableTaskFactory))
                    .FileAndForget("daml/scriptResults/reveal");
                break;
            case NavigationKind.RevealLocation:
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Script results page: {decision.Reveal!.File} is outside the workspace and the library sources; not opened.");
                break;
            case NavigationKind.External:
                VsShellUtilities.OpenSystemBrowser(decision.External!.AbsoluteUri);
                break;
            default:
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Script results page: {decision.Rejection} not followed.");
                break;
        }
    }

    // The page has no frames; any that it tries to load stay empty.
    private void OnFrameNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e) => e.Cancel = true;

    private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (ResultsResources.IsAllowed(e.Request.Uri) || _browserEnvironment is not { } environment)
        {
            return;
        }
        e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Debug, $"Script results page: request for {Shorten(e.Request.Uri)} refused.");
    }

    private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // A navigation stopped on purpose (a clicked link) ends here too; the page is unchanged.
        if (_disposed || _browser?.CoreWebView2 is not { } core || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }
        if (e.IsSuccess)
        {
            // Results with no contracts open on the transactions; the choice itself is kept.
            core.PostWebMessageAsJson(PageMessages.SetView(ResultsPageView.For(_view, Volatile.Read(ref _shownResults))));
        }
        else
        {
            // Whatever was meant to be on screen isn't, so the same results must load next time.
            _updates.Forget();
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Script results page didn't load ({e.WebErrorStatus}).");
        }
    }

    private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    // One theme switch raises this several times; the page is restyled once, after the last.
    private void OnThemeChanged(ThemeChangedEventArgs e) =>
        _joinableTaskFactory.RunAsync(async () =>
        {
            if (!await _themeChanges.WaitAsync().ConfigureAwait(false))
            {
                return;
            }
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed)
            {
                return;
            }
            var theme = CurrentTheme();
            if (theme.Equals(_theme))
            {
                return;
            }
            _theme = theme;
            await TaskScheduler.Default;
            File.WriteAllText(Path.Combine(_folder, ScriptResultsPage.ThemeName), theme.ToCss(), new UTF8Encoding(false));
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            if (!_disposed)
            {
                _browser?.CoreWebView2?.Reload();
            }
        }).FileAndForget("daml/scriptResults/themeChanged");

    private void PrepareFolder(ScriptResultsTheme theme)
    {
        Directory.CreateDirectory(_folder);
        foreach (var asset in new[] { "webview.js", "webview.css", ScriptResultsPage.ShimName })
        {
            File.Copy(Path.Combine(ScriptResultsWindows.AssetsFolder, asset), Path.Combine(_folder, asset), overwrite: true);
        }
        File.WriteAllText(Path.Combine(_folder, ScriptResultsPage.ThemeName), theme.ToCss(), new UTF8Encoding(false));
    }

    private static ScriptResultsTheme CurrentTheme()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var background = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        var foreground = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
        var resources = Application.Current;
        var family = resources?.TryFindResource(VsFonts.EnvironmentFontFamilyKey) as FontFamily;
        var size = resources?.TryFindResource(VsFonts.EnvironmentFontSizeKey) is double pixels && pixels > 0 ? pixels : 12.0;
        return new ScriptResultsTheme(Hex(background), Hex(foreground), family?.Source ?? "Segoe UI", size * 72 / 96);
    }

    private async Task<CoreWebView2Environment> StartAsync(WebView2 browser)
    {
        var environment = await _environment();
        await browser.EnsureCoreWebView2Async(environment);
        return environment;
    }

    // A start that took too long may still finish; the browser it made is then let go of.
    private void DisposeWhenStarted(Task<CoreWebView2Environment> start, WebView2 browser) =>
        _joinableTaskFactory.RunAsync(async () =>
        {
            try
            {
#pragma warning disable VSTHRD003 // started on the UI thread by InitializeAsync, which gave up waiting for it
                await start;
#pragma warning restore VSTHRD003
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Debug, $"Script results: the late browser start failed: {ex.Message}");
            }
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            browser.Dispose();
        }).FileAndForget("daml/scriptResults/lateStart");

    // The installed runtime's version, or null when there is none.
    private static string? InstalledRuntime()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString(null);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or FileNotFoundException or DllNotFoundException or COMException)
        {
            return null;
        }
    }

    private static string Hex(System.Drawing.Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    // A tool window doesn't colour its content: without these a TextBlock is black on a dark theme.
    internal static TextBlock Message(string text)
    {
        var message = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(12),
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        message.SetResourceReference(TextBlock.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        return message;
    }

    private void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Debug, $"Couldn't remove {folder}: {ex.Message}");
        }
    }

    private static string Shorten(string text) => text.Length <= 120 ? text : text.Substring(0, 120) + "…";
}
