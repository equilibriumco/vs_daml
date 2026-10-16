using System.Collections.Generic;
using System.IO;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Tools;

/// <summary>
/// Looks for a command the way the extension and the build both do: the first PATH entry that
/// has it, then the folder its installer uses. Without an extension in the name, <c>.exe</c>
/// is preferred over the <c>.cmd</c> and <c>.bat</c> wrappers in each folder.
/// </summary>
public static class ToolSearch
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    /// <summary>The full path of <paramref name="name"/>, or null when no folder has it.</summary>
    /// <param name="name">The command, e.g. <c>dpm</c> or <c>dpm.cmd</c>.</param>
    /// <param name="pathVariable">The PATH to search; entries that aren't full paths are skipped.</param>
    /// <param name="installFolder">Where the tool's installer puts it, searched last; null for none.</param>
    /// <param name="files">The file system to look in.</param>
    public static string? Find(string name, string? pathVariable, string? installFolder, IDamlFileSystem files)
    {
        var candidates = Path.HasExtension(name) ? [name] : Candidates(name);
        foreach (var folder in Folders(pathVariable, installFolder))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(folder, candidate);
                if (files.FileExists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }

    private static string[] Candidates(string name)
    {
        var names = new string[Extensions.Length];
        for (var i = 0; i < Extensions.Length; i++)
        {
            names[i] = name + Extensions[i];
        }
        return names;
    }

    private static IEnumerable<string> Folders(string? pathVariable, string? installFolder)
    {
        foreach (var entry in (pathVariable ?? "").Split(';'))
        {
            // PATH is user-edited; an entry that isn't a path must not stop the search.
            var folder = entry.Trim().Trim('"');
            if (folder.Length > 0 && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(folder))
            {
                yield return folder;
            }
        }
        if (!string.IsNullOrEmpty(installFolder))
        {
            yield return installFolder!;
        }
    }
}
