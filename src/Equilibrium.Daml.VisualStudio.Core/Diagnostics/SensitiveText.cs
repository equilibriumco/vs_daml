using System;
using System.Text.RegularExpressions;

namespace Equilibrium.Daml.VisualStudio.Core.Diagnostics;

/// <summary>Masks credentials in text that may leave the machine: logs, reports, recorded protocol traffic.</summary>
public static class SensitiveText
{
    /// <summary>What a masked credential is replaced with.</summary>
    public const string SecretPlaceholder = "<redacted secret>";

    // Bearer before JWT, so a token after "Bearer" goes in one piece.
    private static readonly Regex[] Secrets =
    [
        new(@"(?<=://)[^/@\s""']+(?=@)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\b[\w-]*(password|passwd|secret|token|api[_-]?key)[\w-]*\s*[=:]\s*[^\s""',;]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    ];

    /// <summary>
    /// <paramref name="text"/> with URL credentials, bearer tokens, JWTs, GitHub and AWS keys and
    /// <c>name=value</c> pairs whose name suggests a secret masked.
    /// </summary>
    public static string MaskSecrets(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        foreach (var secret in Secrets)
        {
            text = secret.Replace(text, SecretPlaceholder);
        }
        return text;
    }
}
