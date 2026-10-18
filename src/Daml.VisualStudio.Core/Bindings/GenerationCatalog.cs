using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.FileSystem;

namespace Daml.VisualStudio.Core.Bindings;

/// <summary>A generated file, as the manifest of its generation lists it.</summary>
/// <param name="Folder">The generation's folder.</param>
/// <param name="RelativePath">The file's path in it, with backslashes, as the manifest has it.</param>
/// <param name="Sha256">Its SHA-256 when the generation was published.</param>
/// <param name="Provenance">Where the generation came from; null when its manifest doesn't record the DAR's package.</param>
/// <param name="Unchanged">Whether the file on disk still has <paramref name="Sha256"/>, so it is what the generator wrote.</param>
public sealed record GenerationEntry(string Folder, string RelativePath, string Sha256, GenerationProvenance? Provenance, bool Unchanged);

/// <summary>
/// Tells which generation a C# file belongs to: the nearest folder above it with a generation
/// manifest, and only when that manifest lists the file. Whatever else is in a generation's
/// folder isn't the generator's. A manifest is read again when its size or time changes, as
/// when a build publishes the generation again or a first build publishes it at all; a file is
/// hashed again only when its own size or time changed.
/// </summary>
public sealed class GenerationCatalog
{
    private readonly Dictionary<string, Folder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime Written, string Sha256)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>The generation <paramref name="filePath"/> belongs to, or null.</summary>
    public GenerationEntry? Find(string filePath)
    {
        string path;
        try
        {
            path = Path.GetFullPath(filePath ?? throw new ArgumentNullException(nameof(filePath)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        // Generations aren't nested, so a known one above the file is the only candidate; that
        // spares looking for a manifest in every folder in between.
        foreach (var known in KnownGenerationsAbove(path))
        {
            if (Generation(known) is { Manifest: not null } generation)
            {
                return Entry(path, known, generation);
            }
        }
        for (var folder = Path.GetDirectoryName(path); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (Generation(folder!) is { Manifest: not null } generation)
            {
                return Entry(path, folder!.TrimEnd('\\'), generation);
            }
        }
        return null;
    }

    /// <summary>Drops what was read of the generation in <paramref name="folder"/>, so the next lookup reads it again.</summary>
    public void Forget(string folder)
    {
        lock (_gate)
        {
            _folders.Remove(Path.GetFullPath(folder ?? throw new ArgumentNullException(nameof(folder))).TrimEnd('\\'));
        }
    }

    private GenerationEntry? Entry(string path, string folder, Folder generation) =>
        generation.Files!.TryGetValue(path.Substring(folder.Length + 1), out var file)
            ? new GenerationEntry(folder, file.Path, file.Sha256, generation.Provenance, string.Equals(Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
            : null;

    private List<string> KnownGenerationsAbove(string path)
    {
        lock (_gate)
        {
            return _folders.Where(f => f.Value.Manifest is not null && path.Length > f.Key.Length + 1 && path[f.Key.Length] == '\\'
                    && path.StartsWith(f.Key, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Key)
                .OrderByDescending(k => k.Length)
                .ToList();
        }
    }

    private string? Sha256(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }
            lock (_gate)
            {
                if (_hashes.TryGetValue(path, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
                {
                    return known.Sha256;
                }
            }
            var sha256 = Hashing.Sha256File(path);
            lock (_gate)
            {
                _hashes[path] = (file.Length, file.LastWriteTimeUtc, sha256);
            }
            return sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Folder Generation(string folder)
    {
        folder = folder.TrimEnd('\\');
        var path = Path.Combine(folder, GenerationManifest.FileName);
        (bool Exists, long Length, DateTime Written) state;
        try
        {
            var file = new FileInfo(path);
            state = file.Exists ? (true, file.Length, file.LastWriteTimeUtc) : (false, 0, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state = (false, 0, default);
        }
        lock (_gate)
        {
            if (_folders.TryGetValue(folder, out var known) && known.State == state)
            {
                return known;
            }
        }
        var read = new Folder(state, null, null, null);
        try
        {
            if (state.Exists && GenerationManifest.Read(path) is { } manifest)
            {
                var files = new Dictionary<string, GenerationManifestFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in manifest.Files)
                {
                    files[file.Path] = file;
                }
                read = new Folder(state, manifest, GenerationProvenance.From(folder, manifest), files);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being written right now, or out of reach: as if there were none until it changes.
        }
        lock (_gate)
        {
            _folders[folder] = read;
        }
        return read;
    }

    private sealed record Folder(
        (bool Exists, long Length, DateTime Written) State,
        GenerationManifest? Manifest,
        GenerationProvenance? Provenance,
        Dictionary<string, GenerationManifestFile>? Files);
}
