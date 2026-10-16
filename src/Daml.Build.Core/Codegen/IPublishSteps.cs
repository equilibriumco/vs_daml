using System;
using System.IO;

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

    public void Delete(string folder) => Step(() => Directory.Delete(folder, recursive: true));

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
