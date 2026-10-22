using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// The files the results page may load: the page and the four files that sit next to it.
/// Everything else, the network included, is refused.
/// </summary>
public static class ResultsResources
{
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        "/" + ScriptResultsPage.PageName,
        "/webview.js",
        "/webview.css",
        "/" + ScriptResultsPage.ShimName,
        "/" + ScriptResultsPage.ThemeName,
    };

    /// <summary>True when the page may load <paramref name="uri"/>.</summary>
    public static bool IsAllowed(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var resource)
        && ResultsNavigation.IsOrigin(resource)
        && resource.OriginalString.IndexOf("%2e", StringComparison.OrdinalIgnoreCase) < 0
        && resource.OriginalString.IndexOf("/..", StringComparison.Ordinal) < 0
        && Files.Contains(resource.AbsolutePath);
}

/// <summary>
/// The folders whose Daml files a results link may open, e.g. the workspace and the copies of
/// library sources. A link to anywhere else is not followed.
/// </summary>
public sealed class AllowedSources
{
    private readonly IReadOnlyList<string> _roots;

    /// <summary>Allows the files under <paramref name="roots"/>; roots that aren't full paths are ignored.</summary>
    public AllowedSources(IEnumerable<string> roots)
    {
        if (roots is null)
        {
            throw new ArgumentNullException(nameof(roots));
        }
        _roots = roots.Where(IsFullPath).Select(DamlPath.Normalize).ToList();
    }

    /// <summary>True when <paramref name="file"/> is a full path under one of the roots.</summary>
    public bool Contains(string? file) =>
        IsFullPath(file) && _roots.Any(root => DamlPath.IsSameOrUnder(file!, root));

    private static bool IsFullPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        try
        {
            return Path.IsPathRooted(path) && (path!.StartsWith(@"\\", StringComparison.Ordinal) || (path.Length >= 3 && path[1] == ':'));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Whether the server's results HTML still has what the shipped page script relies on. A page
/// without these would show but not work (no view switch, no checkboxes, no notes), so it is
/// replaced by a message instead of being shown half-broken.
/// </summary>
public static class ResultsPageFormat
{
    private static readonly string[] Required =
    [
        "$webviewSrc",
        "$webviewCss",
        "id=\"show_archived\"",
        "id=\"show_detailed_disclosure\"",
        "id=\"note\"",
        "toggle_view()",
        "show_archived_changed()",
        "toggle_detailed_disclosure()",
        "hide_transaction",
    ];

    /// <summary>The parts the page script needs that <paramref name="html"/> lacks; empty when it fits.</summary>
    public static IReadOnlyList<string> MissingParts(string html)
    {
        if (html is null)
        {
            throw new ArgumentNullException(nameof(html));
        }
        return Required.Where(part => html.IndexOf(part, StringComparison.Ordinal) < 0).ToList();
    }
}

/// <summary>Whether the browser control can run.</summary>
public enum RuntimeStatus
{
    /// <summary>A runtime new enough is installed.</summary>
    Ready,

    /// <summary>No runtime, or none that reports a version.</summary>
    Missing,

    /// <summary>A runtime older than the oldest one the window works with.</summary>
    TooOld,
}

/// <summary>The verdict on the installed browser runtime, with what to tell the user.</summary>
/// <param name="Status">Whether it can run.</param>
/// <param name="Message">What to show in the window when it can't; empty when it can.</param>
public sealed record RuntimeCheck(RuntimeStatus Status, string Message);

/// <summary>
/// The Microsoft Edge WebView2 Runtime the results window needs. The runtime updates itself,
/// so one older than the minimum is a broken or frozen install rather than a normal setup.
/// </summary>
public static class WebViewRuntime
{
    /// <summary>The oldest runtime accepted.</summary>
    public static Version Minimum { get; } = new(120, 0, 0, 0);

    /// <summary>Where the runtime is downloaded from.</summary>
    public const string DownloadLink = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>The verdict on a runtime that reports <paramref name="version"/> (null when none is installed).</summary>
    public static RuntimeCheck Check(string? version)
    {
        if (Parse(version) is not { } installed)
        {
            return new RuntimeCheck(RuntimeStatus.Missing,
                $"Script results need the Microsoft Edge WebView2 Runtime, which isn't installed. Install it from {DownloadLink} and open the results again.");
        }
        if (installed < Minimum)
        {
            return new RuntimeCheck(RuntimeStatus.TooOld,
                $"Script results need the Microsoft Edge WebView2 Runtime {Minimum.Major} or later; this machine has {version!.Trim()}. Update it from {DownloadLink} and open the results again.");
        }
        return new RuntimeCheck(RuntimeStatus.Ready, "");
    }

    /// <summary>
    /// What to show when the browser control failed to start, e.g. a runtime missing a feature
    /// the window uses. <paramref name="error"/> is the runtime's own explanation.
    /// </summary>
    public static string Failed(string? error) =>
        $"The Microsoft Edge WebView2 Runtime didn't start{(string.IsNullOrWhiteSpace(error) ? "" : ": " + Shorten(error!.Trim()))}. Update or repair it from {DownloadLink} and open the results again.";

    /// <summary>What to show when the browser control didn't start in time.</summary>
    public static string TimedOut(TimeSpan limit) =>
        $"The Microsoft Edge WebView2 Runtime didn't start within {limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s. Close this window and open the results again; if it keeps happening, repair the runtime from {DownloadLink}.";

    private static string Shorten(string text) => text.Length <= 200 ? text : text.Substring(0, 200) + "…";

    // "154.0.4258.62", possibly followed by a channel name such as " beta".
    private static Version? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        var number = version!.Trim().Split(' ')[0];
        return number.IndexOf('.') > 0 && Version.TryParse(number, out var parsed) ? parsed : null;
    }
}
