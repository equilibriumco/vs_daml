using System;
using System.Collections.Generic;
using System.IO;
using Equilibrium.Daml.Build.Core.Codegen;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Messages;

/// <summary>The kinds of change in <c>workspace/didChangeWatchedFiles</c>.</summary>
public enum FileChangeType
{
    /// <summary>The file appeared.</summary>
    Created = 1,

    /// <summary>The file's content changed.</summary>
    Changed = 2,

    /// <summary>The file is gone.</summary>
    Deleted = 3,
}

/// <summary>One entry of <c>workspace/didChangeWatchedFiles</c>.</summary>
/// <param name="Uri">The file's <c>file:</c> URI.</param>
/// <param name="Type">What happened to it.</param>
public sealed record FileEvent(string Uri, FileChangeType Type);

/// <summary>The file events damlc needs to hear about that Visual Studio doesn't send.</summary>
public static class WatchedFiles
{
    /// <summary>
    /// The events for renaming <paramref name="oldPath"/> to <paramref name="newPath"/>: the old
    /// Daml source deleted and the new one created. Visual Studio reports a rename without the
    /// deletion, and until damlc hears of it, imports of the old module still resolve. Build output
    /// (damlc's own copies under <c>.daml</c>, the C# generator's work folder) is left out.
    /// </summary>
    public static IReadOnlyList<FileEvent> ForRename(string oldPath, string newPath)
    {
        if (oldPath is null)
        {
            throw new ArgumentNullException(nameof(oldPath));
        }
        if (newPath is null)
        {
            throw new ArgumentNullException(nameof(newPath));
        }
        var events = new List<FileEvent>();
        if (IsDamlSource(oldPath))
        {
            events.Add(new FileEvent(new Uri(oldPath).AbsoluteUri, FileChangeType.Deleted));
        }
        if (IsDamlSource(newPath))
        {
            events.Add(new FileEvent(new Uri(newPath).AbsoluteUri, FileChangeType.Created));
        }
        return events;
    }

    private static bool IsDamlSource(string path) =>
        Path.GetExtension(path).Equals(".daml", StringComparison.OrdinalIgnoreCase)
        && path.IndexOf(@"\.daml\", StringComparison.OrdinalIgnoreCase) < 0
        && path.IndexOf($@"\{GenerationPublisher.WorkFolderName}\", StringComparison.OrdinalIgnoreCase) < 0;
}
