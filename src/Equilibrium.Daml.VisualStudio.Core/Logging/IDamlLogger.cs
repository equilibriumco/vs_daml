using System;

namespace Equilibrium.Daml.VisualStudio.Core.Logging;

/// <summary>
/// The only logging entry point for extension code. Implementations must be safe to call from
/// any thread and must never block on the UI thread.
/// </summary>
public interface IDamlLogger
{
    /// <summary>Whether entries at <paramref name="level"/> would be written; lets callers skip building expensive messages.</summary>
    bool IsEnabled(DamlLogLevel level);

    /// <summary>Writes one entry.</summary>
    /// <param name="category">Output channel the entry belongs to.</param>
    /// <param name="level">Severity.</param>
    /// <param name="message">Human-readable text; no trailing newline.</param>
    /// <param name="exception">Optional exception whose details are appended.</param>
    void Log(DamlLogCategory category, DamlLogLevel level, string message, Exception? exception = null);
}
