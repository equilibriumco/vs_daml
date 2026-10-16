using System.IO;

namespace Daml.Build.Core.Codegen;

// The file system operations a publish is made of, so tests can make any one of them fail.
internal interface IPublishSteps
{
    void Move(string from, string to);

    void Delete(string folder);

    void Write(string file, string text);
}

internal sealed class FileSystemPublishSteps : IPublishSteps
{
    public static FileSystemPublishSteps Instance { get; } = new();

    public void Move(string from, string to) => Directory.Move(from, to);

    public void Delete(string folder) => Directory.Delete(folder, recursive: true);

    public void Write(string file, string text) => File.WriteAllText(file, text);
}
