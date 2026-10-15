using System;
using System.IO;

namespace Daml.Build.Core;

/// <summary>
/// Full Windows paths spelled one way, so references to the same file compare equal. Letter
/// case is kept but ignored when comparing; links, junctions and 8.3 names aren't resolved.
/// </summary>
public static class CanonicalPath
{
    /// <summary>Compares canonical paths: Windows ignores letter case.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// <paramref name="path"/> made absolute against <paramref name="baseFolder"/>, with <c>.</c>
    /// and <c>..</c> resolved, backslashes only and no trailing separator except on a root.
    /// </summary>
    public static string Resolve(string path, string baseFolder)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (baseFolder is null)
        {
            throw new ArgumentNullException(nameof(baseFolder));
        }
        var full = Path.GetFullPath(Path.Combine(baseFolder, path.Replace('/', '\\')));
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\') : full;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or below it; both canonical.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (folder is null)
        {
            throw new ArgumentNullException(nameof(folder));
        }
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return path.Length == folder.Length || folder.EndsWith("\\", StringComparison.Ordinal) || path[folder.Length] == '\\';
    }

    /// <summary>Whether <paramref name="path"/>, canonical, is a drive or share root.</summary>
    public static bool IsRoot(string path) =>
        string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);
}
