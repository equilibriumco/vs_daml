namespace Daml.Build.Core.FileSystem;

/// <summary>
/// The file system as project discovery sees it. Only what Core needs, so tests can run
/// against an in-memory implementation. Paths are full Windows paths.
/// </summary>
public interface IDamlFileSystem
{
    /// <summary>True when a file (not a directory) exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>True when a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>Reads a whole text file (UTF-8, BOM honoured).</summary>
    /// <exception cref="System.IO.IOException">The file is missing or can't be read.</exception>
    /// <exception cref="System.UnauthorizedAccessException">Access is denied.</exception>
    string ReadAllText(string path);
}
