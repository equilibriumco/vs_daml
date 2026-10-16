using System.IO;
using System.Text;

namespace Daml.Build.Core.FileSystem;

/// <summary><see cref="IDamlFileSystem"/> over the real disk.</summary>
public sealed class PhysicalFileSystem : IDamlFileSystem
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
}
