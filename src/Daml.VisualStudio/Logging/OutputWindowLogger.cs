using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.Logging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Daml.VisualStudio.Logging;

/// <summary>
/// Writes each category to its own Output window pane; errors also go to the ActivityLog.
/// <see cref="Log"/> only enqueues, so it is cheap and safe on any thread. One coalesced job on
/// the UI thread drains the queue, creating a pane the first time its category shows up so
/// unused categories don't clutter the Output dropdown.
/// </summary>
[Export(typeof(IDamlLogger))]
internal sealed class OutputWindowLogger : IDamlLogger
{
    private const string ActivityLogSource = "Daml for Visual Studio";

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly ConcurrentQueue<(DamlLogCategory Category, string Line)> _queue = new();
    private int _flushScheduled;

    // Only touched on the UI thread. A null value means pane creation failed; lines are dropped.
    private readonly Dictionary<DamlLogCategory, IVsOutputWindowPane?> _panes = new();

    [ImportingConstructor]
    public OutputWindowLogger(JoinableTaskContext joinableTaskContext)
    {
        _joinableTaskFactory = joinableTaskContext.Factory;
    }

    // Becomes configurable with the options page; until then Information and up.
    public DamlLogLevel MinimumLevel { get; set; } = DamlLogLevel.Information;

    public bool IsEnabled(DamlLogLevel level) => level >= MinimumLevel;

    public void Log(DamlLogCategory category, DamlLogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var details = exception is null ? "" : Environment.NewLine + exception;
        _queue.Enqueue((category, $"[{DateTime.Now:HH:mm:ss.fff}] {LevelTag(level)} {message}{details}{Environment.NewLine}"));

        if (level == DamlLogLevel.Error)
        {
            ActivityLog.TryLogError(ActivityLogSource, $"{category}: {message}{details}");
        }

        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            _ = _joinableTaskFactory.RunAsync(FlushAsync);
        }
    }

    private async Task FlushAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);

        // Reset before draining: anything enqueued from here on schedules another pass.
        Volatile.Write(ref _flushScheduled, 0);

        while (_queue.TryDequeue(out var entry))
        {
            var pane = await GetOrCreatePaneAsync(entry.Category);
            pane?.OutputStringThreadSafe(entry.Line);
        }
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
            var outputWindow = await VS.Services.GetOutputWindowAsync();
            var (paneGuid, title) = PaneFor(category);
            ErrorHandler.ThrowOnFailure(outputWindow.CreatePane(ref paneGuid, title, fInitVisible: 1, fClearWithSolution: 0));
            ErrorHandler.ThrowOnFailure(outputWindow.GetPane(ref paneGuid, out pane));
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

    private static (Guid Guid, string Title) PaneFor(DamlLogCategory category) => category switch
    {
        DamlLogCategory.LanguageServer => (PackageGuids.LanguageServerPane, "Daml Language Server"),
        DamlLogCategory.Build => (PackageGuids.BuildPane, "Daml Build"),
        _ => (PackageGuids.ExtensionPane, "Daml"),
    };

    private static string LevelTag(DamlLogLevel level) => level switch
    {
        DamlLogLevel.Trace => "trce",
        DamlLogLevel.Debug => "dbug",
        DamlLogLevel.Information => "info",
        DamlLogLevel.Warning => "warn",
        _ => "fail",
    };
}
