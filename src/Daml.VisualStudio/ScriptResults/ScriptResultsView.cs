using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.ScriptResults;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Task = System.Threading.Tasks.Task;

namespace Daml.VisualStudio.ScriptResults;

/// <summary>
/// The browser half of a results window. The page and its assets are served from a folder of
/// this window's own under one virtual origin; the page's view (table or transaction, the two
/// checkboxes) is kept here and put back after every reload, since new results and theme
/// changes both reload the page. Nothing here ever shows or activates the window.
/// </summary>
internal sealed class ScriptResultsView : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

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
    private WebView2? _browser;
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
        JoinableTaskFactory joinableTaskFactory)
    {
        _resource = resource;
        _view = view;
        _environment = environment;
        _viewChanged = viewChanged;
        _logger = logger;
        _joinableTaskFactory = joinableTaskFactory;
        _folder = Path.Combine(ScriptResultsWindows.DataFolder, "webview", Guid.NewGuid().ToString("N"));
        _host.Content = Text("Loading script results…");
    }

    public FrameworkElement Element => _host;

    /// <summary>Starts the browser and shows a waiting page; a missing runtime is reported in the window.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var theme = CurrentTheme();
        await TaskScheduler.Default;
        PrepareFolder(theme);

        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (_disposed)
        {
            return;
        }
        try
        {
            var environment = await _environment();
            var browser = new WebView2();
            _host.Content = browser;
            await browser.EnsureCoreWebView2Async(environment);
            if (_disposed)
            {
                browser.Dispose();
                return;
            }
            _browser = browser;
            Configure(browser.CoreWebView2);
            _theme = theme;
            VSColorTheme.ThemeChanged += OnThemeChanged;
            await LoadAsync(ScriptResultsPage.Message("Waiting for results…"));
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or FileNotFoundException or COMException or InvalidOperationException)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, $"Script results can't be shown: the browser control didn't start for {_resource}.", ex);
            _host.Content = Text("Script results need the Microsoft Edge WebView2 Runtime, which didn't start: " + ex.Message);
        }
    }

    /// <summary>New results. Shown once a burst of them is over and only if they changed.</summary>
    public async Task UpdateAsync(string html)
    {
        var shown = await _updates.SubmitAsync(html).ConfigureAwait(false);
        if (shown is not null)
        {
            await LoadAsync(ScriptResultsPage.Compose(shown));
        }
    }

    /// <summary>A note from the server above the results, e.g. how long the script has been running.</summary>
    public async Task AddNoteAsync(string text)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        if (!_disposed && _browser?.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson(PageMessages.AddNote(text));
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
        core.Navigate(ScriptResultsPage.Origin + ScriptResultsPage.PageName + "?v=" + version);
    }

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        var message = PageMessages.Parse(e.Source, e.WebMessageAsJson);
        if (message.Rejection is { } rejection)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Script results page: refused {rejection}");
            return;
        }
        _view = _view.Apply(message.Command!, message.Value!);
        _viewChanged(_view);
    }

    // Only our own page may load; links, command: and the rest are dealt with elsewhere or not at all.
    private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(ScriptResultsPage.Origin + ScriptResultsPage.PageName, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Script results page: navigation to {Shorten(e.Uri)} not followed.");
        }
    }

    private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed || _browser?.CoreWebView2 is not { } core)
        {
            return;
        }
        if (e.IsSuccess)
        {
            core.PostWebMessageAsJson(PageMessages.SetView(_view));
        }
        else
        {
            // Whatever was meant to be on screen isn't, so the same results must load next time.
            _updates.Forget();
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Script results page didn't load ({e.WebErrorStatus}).");
        }
    }

    private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private void OnLaunchingExternalUriScheme(object sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e) => e.Cancel = true;

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

    private static string Hex(System.Drawing.Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    private static TextBlock Text(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12),
    };

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
