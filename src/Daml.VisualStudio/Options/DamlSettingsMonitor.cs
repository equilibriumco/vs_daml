using System;
using System.Collections.Generic;
using Daml.VisualStudio.Core.Options;

namespace Daml.VisualStudio.Options;

/// <summary>Carries saved settings and what in them still waits for a server restart.</summary>
internal sealed class DamlSettingsChangedEventArgs : EventArgs
{
    public DamlSettingsChangedEventArgs(DamlSettings settings, IReadOnlyList<string> changesNeedingRestart)
    {
        Settings = settings;
        ChangesNeedingRestart = changesNeedingRestart;
    }

    /// <summary>The settings as saved.</summary>
    public DamlSettings Settings { get; }

    /// <summary>Settings that differ from what the running server was started with; empty when none.</summary>
    public IReadOnlyList<string> ChangesNeedingRestart { get; }

    /// <summary>Whether the running server is out of date.</summary>
    public bool RestartRequired => ChangesNeedingRestart.Count > 0;
}

/// <summary>
/// Compares every save of the options page with the settings the server was last started with,
/// so the client can offer a restart only when the command line would actually change.
/// </summary>
internal sealed class DamlSettingsMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<Action<DamlOptions>> _unsubscribe;
    private DamlSettings? _applied;

    public DamlSettingsMonitor()
        : this(handler => DamlOptions.Saved += handler, handler => DamlOptions.Saved -= handler)
    {
    }

    // Tests pass their own subscription so no VS settings store is needed.
    internal DamlSettingsMonitor(Action<Action<DamlOptions>> subscribe, Action<Action<DamlOptions>> unsubscribe)
    {
        _unsubscribe = unsubscribe;
        subscribe(OnSaved);
    }

    /// <summary>Raised after the options page saves, on the thread that saved.</summary>
    public event EventHandler<DamlSettingsChangedEventArgs>? Changed;

    /// <summary>The server has just been started with <paramref name="settings"/>.</summary>
    public void MarkApplied(DamlSettings settings)
    {
        lock (_gate)
        {
            _applied = settings ?? throw new ArgumentNullException(nameof(settings));
        }
    }

    public void Dispose() => _unsubscribe(OnSaved);

    internal void OnSaved(DamlOptions options) => Report(options.ToSettings());

    internal void Report(DamlSettings saved)
    {
        DamlSettings? applied;
        lock (_gate)
        {
            applied = _applied;
        }
        // Before the first start there is nothing to restart; the start reads the new values.
        var changes = applied is null ? Array.Empty<string>() : DamlSettings.ChangesNeedingRestart(applied, saved);
        Changed?.Invoke(this, new DamlSettingsChangedEventArgs(saved, changes));
    }
}
