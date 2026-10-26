using System;
using System.IO;
using System.Linq;

namespace Equilibrium.Daml.Build.Core.FileSystem;

/// <summary>
/// Windows path helpers with one canonical spelling: full path, backslashes, no trailing
/// separator (except on a drive root). Comparisons ignore case, like the file system does.
/// </summary>
public static class DamlPath
{
    /// <summary>Compares canonical paths the way Windows does: ordinal, case-insensitive.</summary>
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Full path with backslashes, <c>.</c>/<c>..</c> resolved and no trailing separator. The
    /// input must already be rooted: a relative path would resolve against the host process's
    /// current directory, which is never the workspace. Paths over 260 characters need the
    /// host's long-path support (Visual Studio has it).
    /// </summary>
    /// <exception cref="ArgumentException">The path isn't rooted or isn't a path at all.</exception>
    public static string Normalize(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException($"A full path is needed, not '{path}'.", nameof(path));
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

    /// <summary>
    /// The way from <paramref name="baseDirectory"/> to <paramref name="path"/>, such as
    /// <c>..\Contracts\Contracts.msbuildproj</c>, or <c>.</c> for the folder itself; letter case
    /// is ignored. A path on another drive or share has no such way and comes back in full.
    /// <see cref="Resolve"/> turns the result back into the path.
    /// </summary>
    public static string Relative(string baseDirectory, string path)
    {
        var from = Normalize(baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory)));
        var to = Normalize(path ?? throw new ArgumentNullException(nameof(path)));
        if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            return to;
        }
        var fromParts = from.Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
        var toParts = to.Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < fromParts.Length && common < toParts.Length && string.Equals(fromParts[common], toParts[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }
        var parts = Enumerable.Repeat("..", fromParts.Length - common).Concat(toParts.Skip(common)).ToList();
        return parts.Count == 0 ? "." : string.Join("\\", parts);
    }

    /// <summary>
    /// True when <paramref name="path"/> names a place on its own: a drive with its root
    /// (<c>C:\tools</c>) or a share (<c>\\server\share</c>). <c>C:tools</c> and <c>\tools</c> are
    /// rooted but still depend on the current folder of a drive, so they aren't.
    /// </summary>
    public static bool IsFullPath(string? path) =>
        path is { Length: >= 3 }
        && (path.StartsWith(@"\\", StringComparison.Ordinal)
            || (char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')));

    /// <summary>True when <paramref name="path"/> is a drive or share root, such as <c>C:\</c> or <c>\\server\share</c>.</summary>
    public static bool IsRoot(string path)
    {
        var full = Normalize(path);
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase);
    }

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
