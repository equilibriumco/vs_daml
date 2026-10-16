using System;
using System.IO;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Codegen;

// The file system operations a publish is made of, so tests can make any one of them fail.
internal interface IPublishSteps
{
    void Move(string from, string to);

    void Delete(string folder);

    void Write(string file, string text);
}

// A read-only file or a denied folder fails a step like any other IO error does, so the
// publisher's rollback covers it too.
internal sealed class FileSystemPublishSteps : IPublishSteps
{
    public static FileSystemPublishSteps Instance { get; } = new();

    public void Move(string from, string to) => Step(() => Directory.Move(from, to));

    // A generation's files may be past the path limit even when its folder isn't.
    public void Delete(string folder) => Step(() => LongPaths.DeleteFolder(folder));

    public void Write(string file, string text) => Step(() => File.WriteAllText(file, text));

    private static void Step(Action step)
    {
        try
        {
            step();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }
}
