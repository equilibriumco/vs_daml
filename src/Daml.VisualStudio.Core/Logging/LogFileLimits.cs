using System;

namespace Daml.VisualStudio.Core.Logging;

/// <summary>How large the extension's log files grow and how long they stay.</summary>
public sealed class LogFileLimits
{
    /// <summary>Five files of 10 MB, none older than seven days.</summary>
    public static LogFileLimits Default { get; } = new();

    /// <summary>Size at which a new file starts. An entry is never split, so a file can end slightly larger.</summary>
    public long MaxFileBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>Most log files kept in the folder, the one being written included.</summary>
    public int MaxFiles { get; init; } = 5;

    /// <summary>Files not written to for longer than this are deleted.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
}
