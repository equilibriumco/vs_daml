using System;
using System.Collections.Generic;

namespace Daml.VisualStudio.Core.Processes;

/// <summary>What to run and how.</summary>
/// <param name="FileName">
/// Full path of the program. A <c>.cmd</c> or <c>.bat</c> file is run through <c>cmd.exe</c>
/// with its arguments escaped for it.
/// </param>
/// <param name="Arguments">Arguments, each passed to the program exactly as given.</param>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Working directory; <c>null</c> keeps the current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Changes to the inherited environment: a value sets the variable, <c>null</c> removes it.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = NoChanges;

    private static readonly IReadOnlyDictionary<string, string?> NoChanges = new Dictionary<string, string?>();

    /// <summary>
    /// How long <see cref="IProcessRunner.RunAsync"/> waits before killing the process tree;
    /// <c>null</c> waits indefinitely. Ignored by <see cref="IProcessRunner.Start"/>.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
