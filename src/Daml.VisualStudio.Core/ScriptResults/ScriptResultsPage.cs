using System;
using System.Collections.Generic;
using System.Net;

namespace Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// Turns the HTML the language server sends for a script's results into the page the results
/// window loads. Everything the page uses comes from one virtual origin: the server's
/// <c>$webviewSrc</c>/<c>$webviewCss</c> placeholders are pointed at the copies shipped with the
/// extension, and a content security policy keeps the page from reaching anything else.
/// </summary>
public static class ScriptResultsPage
{
    /// <summary>The virtual host the page and its assets are served from; it never resolves on a network.</summary>
    public const string HostName = "daml-webview.invalid";

    /// <summary>The page's origin, with the trailing slash.</summary>
    public const string Origin = "https://" + HostName + "/";

    /// <summary>The file name the composed page is served under.</summary>
    public const string PageName = "results.html";

    /// <summary>The script that maps the page's VS Code calls onto the host.</summary>
    public const string ShimName = "shim.js";

    /// <summary>The stylesheet carrying the editor's colours; see <see cref="ScriptResultsTheme"/>.</summary>
    public const string ThemeName = "theme.css";

    // The page's own onclick/onchange handlers and inline styles need 'unsafe-inline'; apart
    // from that, only our origin, and no connections, frames or forms at all.
    private const string Policy =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; " +
        "script-src " + Origin + " 'unsafe-inline'; style-src " + Origin + " 'unsafe-inline'; img-src " + Origin + " data:; " +
        "connect-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'\">";

    // The charset first: the page is served as a file with none of its own, and the transaction
    // tree draws with characters outside ASCII. The shim before the page's scripts: webview.js
    // asks for the VS Code API as soon as it loads.
    private const string Head =
        "<meta charset=\"utf-8\">" +
        Policy +
        "<script src=\"" + Origin + ShimName + "\"></script>" +
        "<link rel=\"stylesheet\" href=\"" + Origin + ThemeName + "\">";

    /// <summary>The server's results HTML, ready to load.</summary>
    public static string Compose(string html)
    {
        if (html is null)
        {
            throw new ArgumentNullException(nameof(html));
        }
        var page = html
            .Replace("$webviewSrc", Origin + "webview.js")
            .Replace("$webviewCss", Origin + "webview.css");
        var head = page.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return head >= 0 ? page.Insert(head + "<head>".Length, Head) : Head + page;
    }

    /// <summary>
    /// The page shown instead of results whose HTML lacks what the shipped page script relies
    /// on: such a page would load but not work, so it isn't loaded at all.
    /// </summary>
    /// <param name="missing">What the results lack, from <see cref="ResultsPageFormat.MissingParts"/>.</param>
    public static string Unsupported(IEnumerable<string> missing)
    {
        if (missing is null)
        {
            throw new ArgumentNullException(nameof(missing));
        }
        return Message(
            "These script results come from a Daml SDK whose results page this version of the extension doesn't know " +
            $"(missing: {string.Join(", ", missing)}). Update the Daml extension, or use the SDK version the project was set up with.");
    }

    /// <summary>A page that only says <paramref name="text"/>, e.g. while results are on their way.</summary>
    public static string Message(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        return "<!DOCTYPE html><html><head>" + Head + "</head><body><p>" + WebUtility.HtmlEncode(text) + "</p></body></html>";
    }
}
