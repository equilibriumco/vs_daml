using System;

namespace Equilibrium.Daml.VisualStudio.Core.ScriptResults;

/// <summary>What the results window does with a navigation its page starts.</summary>
public enum NavigationKind
{
    /// <summary>The results page itself (a reload, or an anchor on it): let it happen.</summary>
    OwnPage,

    /// <summary>A link to a place in a Daml source the user clicked: open it in the editor instead.</summary>
    RevealLocation,

    /// <summary>A web link the user clicked: open it in the system browser instead.</summary>
    External,

    /// <summary>Anything else: stop it.</summary>
    Blocked,
}

/// <summary>A navigation's verdict, with what to open or why it was stopped.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Reveal">The source location, for <see cref="NavigationKind.RevealLocation"/>.</param>
/// <param name="External">The web address, for <see cref="NavigationKind.External"/>.</param>
/// <param name="Rejection">Why it was stopped, for <see cref="NavigationKind.Blocked"/>.</param>
public sealed record NavigationDecision(NavigationKind Kind, RevealRequest? Reveal, Uri? External, string? Rejection);

/// <summary>
/// Decides where the results page may go. Only the page itself loads inside the window. A
/// source link opens the source in the editor and a web link opens the browser, but only when
/// the user clicked it; nothing the page does by itself leaves the window.
/// </summary>
public static class ResultsNavigation
{
    /// <summary>The verdict on a navigation to <paramref name="uri"/>.</summary>
    /// <param name="uri">Where the page is going.</param>
    /// <param name="userInitiated">Whether a user's click started it.</param>
    public static NavigationDecision Classify(string? uri, bool userInitiated)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return Blocked("an empty address");
        }
        if (IsOwnPage(uri))
        {
            return new NavigationDecision(NavigationKind.OwnPage, null, null, null);
        }
        if (uri!.StartsWith(RevealRequest.Prefix, StringComparison.Ordinal))
        {
            if (!userInitiated)
            {
                return Blocked("a source link the page followed by itself");
            }
            return RevealRequest.TryParse(uri, out var reveal, out var rejection)
                ? new NavigationDecision(NavigationKind.RevealLocation, reveal, null, null)
                : Blocked(rejection!);
        }
        if (Uri.TryCreate(uri, UriKind.Absolute, out var web)
            && (web.Scheme == Uri.UriSchemeHttp || web.Scheme == Uri.UriSchemeHttps)
            && !string.Equals(web.Host, ScriptResultsPage.HostName, StringComparison.OrdinalIgnoreCase))
        {
            if (!userInitiated)
            {
                return Blocked($"{Shorten(uri)}, which the page went to by itself");
            }
            if (web.UserInfo.Length > 0)
            {
                return Blocked("a web link with a user name or password in it");
            }
            return new NavigationDecision(NavigationKind.External, null, web, null);
        }
        return Blocked(Shorten(uri));
    }

    /// <summary>
    /// True for the results page at the extension's virtual origin, whatever its query or
    /// anchor: https, that host, the default port, no user, and the page's own path.
    /// </summary>
    public static bool IsOwnPage(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var page)
        && IsOrigin(page)
        && string.Equals(page.AbsolutePath, "/" + ScriptResultsPage.PageName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsOrigin(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, ScriptResultsPage.HostName, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0;

    private static NavigationDecision Blocked(string why) => new(NavigationKind.Blocked, null, null, why);

    private static string Shorten(string text) => text.Length <= 120 ? text : text.Substring(0, 120) + "…";
}
