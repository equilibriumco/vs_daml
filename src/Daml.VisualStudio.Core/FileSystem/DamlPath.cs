using System;
using System.Collections.Generic;
using System.IO;

namespace Daml.VisualStudio.Core.FileSystem;

/// <summary>
/// Windows path helpers with one canonical spelling: full path, backslashes, no trailing
/// separator (except on a drive root). Comparisons ignore case, like the file system does.
/// </summary>
public static class DamlPath
{
    /// <summary>Compares canonical paths the way Windows does: ordinal, case-insensitive.</summary>
    public static IEqualityComparer<string> Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>Full path with backslashes, <c>.</c>/<c>..</c> resolved and no trailing separator.</summary>
    public static string Normalize(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        var full = Path.GetFullPath(path.Replace('/', '\\'));
        var root = Path.GetPathRoot(full);
        return full.Length > root?.Length ? full.TrimEnd('\\') : full;
    }

    /// <summary>
    /// Resolves a path written in a manifest (relative, with forward slashes, or rooted)
    /// against <paramref name="baseDirectory"/>.
    /// </summary>
    public static string Resolve(string baseDirectory, string path) =>
        Normalize(Path.Combine(baseDirectory, path.Replace('/', '\\')));

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or lies inside it.</summary>
    public static bool IsSameOrUnder(string path, string directory)
    {
        var p = Normalize(path);
        var d = Normalize(directory);
        if (p.Equals(d, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var prefix = d.EndsWith("\\", StringComparison.Ordinal) ? d : d + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
