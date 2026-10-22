using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Equilibrium.Daml.VisualStudio.Core.Logging;

/// <summary>
/// Appends log text to <c>daml-&lt;local time&gt;-&lt;process id&gt;.log</c> files in one folder,
/// starting a new file at the size limit and deleting the extension's files beyond the count
/// and age limits whenever a file starts. Each process writes only its own files; the handle is
/// held only while a batch is written, so other processes can read and delete the files.
/// Not thread-safe: one writer at a time.
/// </summary>
public sealed class RollingLogFile
{
    // Strict, so pruning can never touch a file the extension didn't name.
    private static readonly Regex OwnFileName = new(@"^daml-\d{8}-\d{6}-\d+(-\d+)?\.log$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly int _processId;
    private readonly TimeProvider _time;
    private readonly LogFileLimits _limits;

    /// <summary>Creates the writer; nothing touches the disk before the first <see cref="Append"/>.</summary>
    public RollingLogFile(string folder, int processId, TimeProvider time, LogFileLimits? limits = null)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _processId = processId;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _limits = limits ?? LogFileLimits.Default;
    }

    /// <summary><c>%LOCALAPPDATA%\EquilibriumDamlTools\logs</c>, beside the extension's other caches.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EquilibriumDamlTools", "logs");

    /// <summary>The folder the files go to.</summary>
    public string Folder { get; }

    /// <summary>The file being written, or null before the first entry.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>The extension's log files in <paramref name="folder"/>, most recently written first.</summary>
    public static IReadOnlyList<FileInfo> Files(string folder)
    {
        var directory = new DirectoryInfo(folder ?? throw new ArgumentNullException(nameof(folder)));
        if (!directory.Exists)
        {
            return [];
        }
        return directory.EnumerateFiles("daml-*.log")
            .Where(file => OwnFileName.IsMatch(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Appends <paramref name="text"/> as UTF-8, first starting a new file if it would pass the size limit.</summary>
    /// <exception cref="IOException">The folder or file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public void Append(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var bytes = Utf8.GetBytes(text);
        if (CurrentPath is null)
        {
            StartFile();
        }

        FileStream stream;
        try
        {
            stream = Open(CurrentPath!);
        }
        catch (DirectoryNotFoundException)
        {
            // Someone deleted the whole folder; start over in a new one.
            StartFile();
            stream = Open(CurrentPath!);
        }
        try
        {
            if (stream.Length > 0 && stream.Length + bytes.Length > _limits.MaxFileBytes)
            {
                stream.Dispose();
                StartFile();
                stream = Open(CurrentPath!);
            }
            stream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>Deletes the extension's files that are too old or beyond the count limit, except the current one.</summary>
    public void Prune()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _limits.Retention;
        var kept = CurrentPath is null ? 0 : 1;
        foreach (var file in Files(Folder))
        {
            if (string.Equals(file.FullName, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (file.LastWriteTimeUtc >= cutoff && kept < _limits.MaxFiles)
            {
                kept++;
                continue;
            }
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process may have it open; the next prune tries again.
            }
        }
    }

    private void StartFile()
    {
        Directory.CreateDirectory(Folder);
        var stem = $"daml-{_time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{_processId.ToString(CultureInfo.InvariantCulture)}";
        var path = Path.Combine(Folder, stem + ".log");
        for (var n = 1; File.Exists(path); n++)
        {
            path = Path.Combine(Folder, $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}.log");
        }
        CurrentPath = path;
        Prune();
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
}
