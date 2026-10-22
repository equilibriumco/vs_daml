using System;
using System.Collections.Generic;
using Equilibrium.Daml.VisualStudio.Core.Logging;

namespace Equilibrium.Daml.TestHelpers;

/// <summary>One entry a <see cref="RecordingLogger"/> received.</summary>
public sealed record LogEntry(DamlLogCategory Category, DamlLogLevel Level, string Message, Exception? Exception);

/// <summary>Keeps every entry logged, for tests that assert on what was written and at which level.</summary>
public sealed class RecordingLogger : IDamlLogger
{
    private readonly DamlLogLevel _minimum;
    private readonly List<LogEntry> _entries = new();

    /// <summary>Records everything from <paramref name="minimum"/> up; everything by default.</summary>
    public RecordingLogger(DamlLogLevel minimum = DamlLogLevel.Trace) => _minimum = minimum;

    /// <summary>The entries so far, oldest first; a snapshot, safe to read while logging goes on.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public bool IsEnabled(DamlLogLevel level) => level >= _minimum;

    public void Log(DamlLogCategory category, DamlLogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }
        lock (_entries)
        {
            _entries.Add(new LogEntry(category, level, message, exception));
        }
    }
}
