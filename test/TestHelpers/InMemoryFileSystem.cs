using System.Collections.Generic;
using System.IO;
using Daml.VisualStudio.Core.FileSystem;

namespace Daml.TestHelpers;

/// <summary>
/// In-memory <see cref="IDamlFileSystem"/>. Adding a file also creates its parent directories;
/// paths are normalised and compared without case, like on disk.
/// </summary>
public sealed class InMemoryFileSystem : IDamlFileSystem
{
    private readonly Dictionary<string, string> _files = new(DamlPath.Comparer);
    private readonly HashSet<string> _directories = new(DamlPath.Comparer);

    /// <summary>Adds or replaces a file, creating its parent directories.</summary>
    public InMemoryFileSystem AddFile(string path, string content)
    {
        var full = DamlPath.Normalize(path);
        _files[full] = content;
        AddDirectory(Path.GetDirectoryName(full)!);
        return this;
    }

    /// <summary>Adds a directory and its parents.</summary>
    public InMemoryFileSystem AddDirectory(string path)
    {
        for (var dir = DamlPath.Normalize(path); dir is not null && _directories.Add(dir); dir = Path.GetDirectoryName(dir))
        {
        }
        return this;
    }

    /// <summary>Removes a file; its directories stay.</summary>
    public void RemoveFile(string path) => _files.Remove(DamlPath.Normalize(path));

    /// <inheritdoc />
    public bool FileExists(string path) => _files.ContainsKey(DamlPath.Normalize(path));

    /// <inheritdoc />
    public bool DirectoryExists(string path) => _directories.Contains(DamlPath.Normalize(path));

    /// <inheritdoc />
    public string ReadAllText(string path) =>
        _files.TryGetValue(DamlPath.Normalize(path), out var content)
            ? content.TrimStart('\uFEFF') // File.ReadAllText drops a byte-order mark; so does this
            : throw new FileNotFoundException("No such file in the in-memory file system.", path);
}
