using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>Copies a library file somewhere Visual Studio can open it.</summary>
public interface ILibraryCopier
{
    /// <summary>
    /// Makes <paramref name="target"/> a read-only copy of <paramref name="source"/>, refreshed
    /// when its content changes. Returns false when there is nothing to copy or the copy failed.
    /// </summary>
    bool CopyReadOnly(string source, string target);
}

/// <summary>
/// Visual Studio can't open files whose path is 260 characters or longer, and damlc points
/// definitions in libraries at exactly such files: the package database names its folders after
/// the full module name and a 64-character package hash. Definition answers that point there are
/// rewritten to a short, read-only copy of the file; everything else passes unchanged.
/// </summary>
public sealed class LongPathRedirector
{
    /// <summary>Paths this long or longer are redirected.</summary>
    public const int MaxPath = 260;

    private readonly string _copyRoot;
    private readonly ILibraryCopier _copier;
    private readonly ConcurrentDictionary<string, string> _originals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a redirector that keeps its copies under <paramref name="copyRoot"/>.</summary>
    public LongPathRedirector(string copyRoot, ILibraryCopier copier)
    {
        _copyRoot = copyRoot ?? throw new ArgumentNullException(nameof(copyRoot));
        _copier = copier ?? throw new ArgumentNullException(nameof(copier));
    }

    /// <summary>
    /// The answer to a definition-like request (a location, an array of locations or location
    /// links, or null) with long file targets replaced; the same instance when nothing changed.
    /// </summary>
    public JToken? Redirect(JToken? result)
    {
        switch (result)
        {
            case JArray items:
                var redirected = items.Select(RedirectItem).ToList();
                return redirected.Where((item, i) => !ReferenceEquals(item, items[i])).Any() ? new JArray(redirected) : items;
            case JObject item:
                return RedirectItem(item);
            default:
                return result;
        }
    }

    /// <summary>Maps document requests for a short library copy back to its server-side source URI.</summary>
    /// <param name="parameters">Request or notification parameters; left unchanged when no mapping exists.</param>
    public JToken OriginalDocument(JToken parameters)
    {
        if (parameters is null) { throw new ArgumentNullException(nameof(parameters)); }
        if (parameters is not JObject || (string?)parameters["textDocument"]?["uri"] is not { } uri
            || LspUri.ToLocalPath(uri) is not { } path || !_originals.TryGetValue(path, out var original))
        {
            return parameters;
        }
        var copy = parameters.DeepClone();
        copy["textDocument"]!["uri"] = original;
        return copy;
    }

    private JToken RedirectItem(JToken item)
    {
        if (item is not JObject location)
        {
            return item;
        }
        foreach (var key in new[] { "uri", "targetUri" })
        {
            if ((string?)location[key] is { } uri && Map(uri) is { } mapped)
            {
                var copy = (JObject)location.DeepClone();
                copy[key] = mapped;
                return copy;
            }
        }
        return item;
    }

    // The URI of the short copy, or null to keep the original.
    private string? Map(string uri)
    {
        if (LspUri.ToLocalPath(uri) is not { } path)
        {
            return null;
        }
        if (path.Length < MaxPath)
        {
            return null;
        }
        var target = Path.Combine(_copyRoot, Hash(path), Path.GetFileName(path));
        if (!_copier.CopyReadOnly(path, target)) { return null; }
        _originals[target] = new Uri(path).AbsoluteUri;
        return new Uri(target).AbsoluteUri;
    }

    // One folder per source path, so equally named modules of different packages don't collide.
    private static string Hash(string path)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return string.Concat(bytes.Take(6).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}

/// <summary>Copies library files on disk, reading their long paths through the <c>\\?\</c> prefix.</summary>
public sealed class LibraryCopier : ILibraryCopier
{
    /// <inheritdoc />
    public bool CopyReadOnly(string source, string target)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }
        try
        {
            var from = Extended(source);
            if (!File.Exists(from))
            {
                return false;
            }
            var written = File.GetLastWriteTimeUtc(from);
            // Archive timestamps and size alone cannot distinguish successive library versions.
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(from).Length && SameContents(from, target))
            {
                return true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }
            File.Copy(from, target, overwrite: true);
            // The copy carries the source's time, so a newer source shows up as newer.
            File.SetLastWriteTimeUtc(target, written);
            File.SetAttributes(target, FileAttributes.ReadOnly);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameContents(string source, string target)
    {
        using var hash = SHA256.Create();
        using var from = File.OpenRead(source);
        using var to = File.OpenRead(target);
        return hash.ComputeHash(from).SequenceEqual(hash.ComputeHash(to));
    }

    internal static string Extended(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.Length < LongPathRedirector.MaxPath ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
}
