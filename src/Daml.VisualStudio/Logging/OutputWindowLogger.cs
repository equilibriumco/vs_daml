using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.Build.Core.Diagnostics;
using Daml.VisualStudio.Core.Logging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Daml.VisualStudio.Logging;

/// <summary>
/// Writes each category to its own Output window pane and to the extension's log files; errors
/// also go to the ActivityLog.
/// <see cref="Log"/> only enqueues, so it is cheap and safe on any thread. A single job on the
/// UI thread drains the queue in bounded batches, merging consecutive lines of a category into
/// one write, and creates a pane the first time its category shows up so unused categories
/// don't clutter the Output dropdown.
/// </summary>
[Export(typeof(IDamlLogger))]
internal sealed class OutputWindowLogger : IDamlLogger
{
    private const string ActivityLogSource = "Daml for Visual Studio";

    // Flushes are tracked so pending output can be awaited (tests, shutdown) instead of guessed at.
    private readonly JoinableTaskCollection _flushes;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly ConcurrentQueue<(DamlLogCategory Category, string Line)> _queue = new();
    private int _flushScheduled;

    // Only touched on the UI thread. A null value means pane creation failed; lines are dropped.
    private readonly Dictionary<DamlLogCategory, IVsOutputWindowPane?> _panes = new();

    /// <summary>Most queue entries one pass writes before giving the UI thread back.</summary>
    internal const int MaxEntriesPerPass = 256;

    private readonly Func<Guid, string, Task<IVsOutputWindowPane?>> _createPaneAsync;

    // The same entries also go to rotating files, which outlive the session and the Output window.
    private readonly LogFileSink? _files;

    [ImportingConstructor]
    public OutputWindowLogger(JoinableTaskContext joinableTaskContext)
        : this(joinableTaskContext, null, new LogFileSink(new RollingLogFile(RollingLogFile.DefaultFolder, CurrentProcessId(), TimeProvider.System), TimeProvider.System))
    {
    }

    // Tests pass their own pane factory to control when pane creation completes, and their own
    // log folder or none.
    internal OutputWindowLogger(JoinableTaskContext joinableTaskContext, Func<Guid, string, Task<IVsOutputWindowPane?>>? createPaneAsync, LogFileSink? files = null)
    {
        _flushes = joinableTaskContext.CreateCollection();
        _joinableTaskFactory = joinableTaskContext.CreateFactory(_flushes);
        _createPaneAsync = createPaneAsync ?? CreateOutputPaneAsync;
        _files = files;
    }

    /// <summary>The folder of the log files, or null when entries only go to the Output window.</summary>
    internal string? LogFolder => _files?.Folder;

    /// <summary>Completes once everything logged so far is in the log files.</summary>
    internal Task DrainFilesAsync() => _files?.DrainAsync() ?? Task.CompletedTask;

    // Information and up. The log level on the options page is the server's, not this pane's.
    public DamlLogLevel MinimumLevel { get; set; } = DamlLogLevel.Information;

    public bool IsEnabled(DamlLogLevel level) => level >= MinimumLevel;

    public void Log(DamlLogCategory category, DamlLogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        message = SecretRedaction.Redact(message);
        var details = exception is null ? "" : SecretRedaction.Redact(Environment.NewLine + exception);
        Enqueue(category, $"[{DateTime.Now:HH:mm:ss.fff}] {LevelTag(level)} {message}{details}{Environment.NewLine}");
        _files?.Write(category, level, message + details);

        if (level == DamlLogLevel.Error)
        {
            ActivityLog.TryLogError(ActivityLogSource, $"{category}: {message}{details}");
        }
    }

    /// <summary>
    /// Writes <paramref name="line"/> as it is, in order with the log entries around it. The
    /// Output window only jumps to <c>file(line,col): …</c> when the line starts with the path.
    /// </summary>
    internal void WritePlainLine(DamlLogCategory category, string line)
    {
        line = SecretRedaction.Redact(line);
        Enqueue(category, line + Environment.NewLine);
        _files?.Write(category, DamlLogLevel.Information, line);
    }

    /// <summary>Opens the Output window on the pane of <paramref name="category"/>, creating it if needed.</summary>
    internal async Task ShowAsync(DamlLogCategory category)
    {
        var pane = await GetOrCreatePaneAsync(category);
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        await VS.Windows.ShowToolWindowAsync(new Guid(ToolWindowGuids80.Outputwindow));
        pane?.Activate();
    }

    private void Enqueue(DamlLogCategory category, string text)
    {
        _queue.Enqueue((category, text));
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            _ = _joinableTaskFactory.RunAsync(FlushAsync);
        }
    }

    /// <summary>Completes once everything logged so far has reached its pane.</summary>
    internal Task DrainAsync() => _flushes.JoinTillEmptyAsync();

    // The only flusher while _flushScheduled is 1, so lines can't overtake each other even when
    // a pane creation awaits. Each pass writes a bounded batch, then yields the UI thread.
    private async Task FlushAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);

        while (true)
        {
            await WriteBatchAsync();

            if (_queue.IsEmpty)
            {
                Volatile.Write(ref _flushScheduled, 0);
                // Something enqueued just before the reset saw the flag still set and didn't
                // schedule a flush; take it on here unless a new Log call already has.
                if (_queue.IsEmpty || Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
                {
                    return;
                }
            }

            await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
        }
    }

    // Consecutive lines of one category go out as a single pane write.
    private async Task WriteBatchAsync()
    {
        var text = new StringBuilder();
        DamlLogCategory? category = null;
        for (var taken = 0; taken < MaxEntriesPerPass && _queue.TryDequeue(out var entry); taken++)
        {
            if (category is { } current && current != entry.Category)
            {
                await WriteAsync(current, text);
            }
            category = entry.Category;
            text.Append(entry.Line);
        }
        if (category is { } last)
        {
            await WriteAsync(last, text);
        }
    }

    private async Task WriteAsync(DamlLogCategory category, StringBuilder text)
    {
        var pane = await GetOrCreatePaneAsync(category);
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        pane?.OutputStringThreadSafe(text.ToString());
        text.Clear();
    }

    private async Task<IVsOutputWindowPane?> GetOrCreatePaneAsync(DamlLogCategory category)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        if (_panes.TryGetValue(category, out var existing))
        {
            return existing;
        }

        IVsOutputWindowPane? pane = null;
        try
        {
            var (paneGuid, title) = PaneFor(category);
            pane = await _createPaneAsync(paneGuid, title);
            await _joinableTaskFactory.SwitchToMainThreadAsync();
        }
        catch (Exception ex)
        {
            // Logging must never take the extension down; record why and drop this category.
            ActivityLog.TryLogError(ActivityLogSource, $"Could not create the Output pane for {category}: {ex}");
            pane = null;
        }

        _panes[category] = pane;
        return pane;
    }

    private async Task<IVsOutputWindowPane?> CreateOutputPaneAsync(Guid paneGuid, string title)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        var outputWindow = await VS.Services.GetOutputWindowAsync();
        ErrorHandler.ThrowOnFailure(outputWindow.CreatePane(ref paneGuid, title, fInitVisible: 1, fClearWithSolution: 0));
        ErrorHandler.ThrowOnFailure(outputWindow.GetPane(ref paneGuid, out var pane));
        return pane;
    }

    private static (Guid Guid, string Title) PaneFor(DamlLogCategory category) => category switch
    {
        DamlLogCategory.LanguageServer => (PackageGuids.LanguageServerPane, "Daml Language Server"),
        DamlLogCategory.Build => (PackageGuids.BuildPane, "Daml Build"),
        _ => (PackageGuids.ExtensionPane, "Daml"),
    };

    private static int CurrentProcessId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.Id;
    }

    private static string LevelTag(DamlLogLevel level) => level switch
    {
        DamlLogLevel.Trace => "trce",
        DamlLogLevel.Debug => "dbug",
        DamlLogLevel.Information => "info",
        DamlLogLevel.Warning => "warn",
        _ => "fail",
    };
}
