using System;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>
/// File URIs as the two sides spell them: Visual Studio escapes the drive colon
/// (<c>file:///m%3A/...</c>) and damlc echoes that back, while <see cref="Uri.LocalPath"/>
/// turns the escaped form into <c>/m:/...</c>, which isn't a Windows path at all.
/// </summary>
internal static class LspUri
{
    /// <summary>The Windows path of a <c>file:</c> URI, or null for any other URI or none.</summary>
    public static string? ToLocalPath(string? uri)
    {
        if (uri is null)
        {
            return null;
        }
        var unescapedDrive = uri.Replace("%3A", ":").Replace("%3a", ":");
        return Uri.TryCreate(unescapedDrive, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;
    }
}
