using System;
using System.Text.RegularExpressions;

namespace Daml.Build.Core.Diagnostics;

/// <summary>Keeps credentials out of what the build reports, since tool output quotes proxy and registry addresses.</summary>
public static class SecretRedaction
{
    // The user and password of "scheme://user:password@host".
    private static readonly Regex UserInfo = new(@"(?<=://)[^/@\s""']+@", RegexOptions.CultureInvariant);

    /// <summary><paramref name="text"/> with the credentials of every URL in it replaced by <c>***</c>.</summary>
    public static string Redact(string text) =>
        UserInfo.Replace(text ?? throw new ArgumentNullException(nameof(text)), "***@");
}
