using System;

namespace Daml.VisualStudio.Core.Logging;

/// <summary>Logger that drops everything. For tests and for code that runs before logging is wired up.</summary>
public sealed class NullDamlLogger : IDamlLogger
{
    /// <summary>The shared instance.</summary>
    public static NullDamlLogger Instance { get; } = new();

    private NullDamlLogger()
    {
    }

    /// <inheritdoc />
    public bool IsEnabled(DamlLogLevel level) => false;

    /// <inheritdoc />
    public void Log(DamlLogCategory category, DamlLogLevel level, string message, Exception? exception = null)
    {
    }
}
