using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Daml.Build.Core.FileSystem;

/// <summary>
/// Paths past Windows' 260-character limit, which generated code reaches easily: a Daml module
/// tree under the project's folder, then the generator's package registration file with a
/// 64-character package id in its name. .NET Framework, which MSBuild.exe runs the tasks on, only
/// takes such a path in its extended <c>\\?\</c> form unless long paths are turned on in Windows;
/// .NET takes either. A host built for .NET Framework before 4.6.2, as the 32-bit MSBuild.exe of
/// Visual Studio is, keeps the old path rules and rejects the extended form outright, so there
/// every path stays plain and paths past the limit fail as they always did. Only full,
/// normalized paths have an extended form.
/// </summary>
public static class LongPaths
{
    /// <summary>The longest path Windows takes without long paths turned on.</summary>
    public const int MaxPathLength = 259;

    // Folders must leave room for an 8.3 file name inside them.
    private const int MaxFolderLength = 247;

    private const string Prefix = @"\\?\";

    // Asked once, of a folder that surely exists: whether it can be seen in its extended form.
    private static readonly Lazy<bool> Supported = new(() =>
        Directory.Exists(Extended(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))));

    /// <summary>Whether this process takes paths in their extended form.</summary>
    public static bool ExtendedPathsWork => Supported.Value;

    /// <summary>The extended form of a full path: <c>\\?\C:\…</c>, or <c>\\?\UNC\server\share\…</c>.</summary>
    public static string Extended(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return path;
        }
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? Prefix + @"UNC\" + path.Substring(2) : Prefix + path;
    }

    /// <summary>
    /// <paramref name="path"/> as it is when Windows takes it so, and its extended form when it is
    /// too long; messages keep the plain path wherever they can.
    /// </summary>
    public static string Safe(string path) => Safe(path, ExtendedPathsWork);

    internal static string Safe(string path, bool extended) =>
        extended && (path ?? throw new ArgumentNullException(nameof(path))).Length > MaxFolderLength ? Extended(path) : path;

    /// <summary>Whether there is a file at <paramref name="path"/>, however long it is.</summary>
    public static bool FileExists(string path) => File.Exists(Safe(path));

    /// <summary>Every file under <paramref name="folder"/>, relative to it, however deep.</summary>
    public static IReadOnlyList<string> RelativeFiles(string folder) => RelativeFiles(folder, ExtendedPathsWork);

    internal static IReadOnlyList<string> RelativeFiles(string folder, bool extended)
    {
        var root = extended ? Extended(folder) : folder;
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar))
            .ToList();
    }

    /// <summary>Every folder under <paramref name="folder"/>, relative to it, however deep.</summary>
    public static IReadOnlyList<string> RelativeFolders(string folder)
    {
        var root = ExtendedPathsWork ? Extended(folder) : folder;
        return Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar))
            .ToList();
    }

    /// <summary>Deletes <paramref name="folder"/> and everything in it, however deep.</summary>
    public static void DeleteFolder(string folder) => DeleteFolder(folder, ExtendedPathsWork);

    internal static void DeleteFolder(string folder, bool extended) => Directory.Delete(extended ? Extended(folder) : folder, recursive: true);
}
