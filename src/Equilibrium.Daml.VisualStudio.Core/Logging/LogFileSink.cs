using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Equilibrium.Daml.VisualStudio.Core.Logging;

/// <summary>
/// Puts log entries into a <see cref="RollingLogFile"/> without making the caller wait for the
/// disk: <see cref="Write"/> only queues, and one job on the thread pool at a time writes what
/// has queued up, in order. If the disk refuses, the entries are dropped; logging never throws.
/// </summary>
public sealed class LogFileSink
{
    // One batch per write; a burst of output doesn't turn into thousands of file opens.
    private const int MaxBatchChars = 256 * 1024;

    private readonly RollingLogFile _file;
    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly object _gate = new();
    private Task _drain = Task.CompletedTask;
    private bool _draining;

    /// <summary>Creates a sink writing to <paramref name="file"/>, stamping entries with <paramref name="time"/>.</summary>
    public LogFileSink(RollingLogFile file, TimeProvider time)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The folder the files go to.</summary>
    public string Folder => _file.Folder;

    /// <summary>Queues one entry. Safe on any thread; never blocks on the disk.</summary>
    public void Write(DamlLogCategory category, DamlLogLevel level, string message)
    {
        _queue.Enqueue(Format(_time.GetLocalNow(), category, level, message ?? ""));
        lock (_gate)
        {
            if (_draining)
            {
                return;
            }
            _draining = true;
            _drain = Task.Run(Drain);
        }
    }

    /// <summary>Completes once everything queued so far is on disk, or was dropped.</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            Task drain;
            lock (_gate)
            {
                if (!_draining)
                {
                    return;
                }
                drain = _drain;
            }
            await drain.ConfigureAwait(false);
        }
    }

    private void Drain()
    {
        var batch = new StringBuilder();
        while (true)
        {
            while (batch.Length < MaxBatchChars && _queue.TryDequeue(out var entry))
            {
                batch.Append(entry);
            }
            if (batch.Length > 0)
            {
                try
                {
                    _file.Append(batch.ToString());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
                {
                    // Nowhere to report it: the log is where it would go.
                }
                batch.Clear();
            }
            lock (_gate)
            {
                if (_queue.IsEmpty)
                {
                    _draining = false;
                    return;
                }
            }
        }
    }

    private static string Format(DateTimeOffset time, DamlLogCategory category, DamlLogLevel level, string message)
    {
        var text = message.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        return $"{time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {Tag(level)} [{category}] {text}\r\n";
    }

    private static string Tag(DamlLogLevel level) => level switch
    {
        DamlLogLevel.Trace => "trce",
        DamlLogLevel.Debug => "dbug",
        DamlLogLevel.Information => "info",
        DamlLogLevel.Warning => "warn",
        _ => "fail",
    };
}
