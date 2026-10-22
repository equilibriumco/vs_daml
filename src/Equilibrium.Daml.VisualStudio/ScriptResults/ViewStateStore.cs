using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.ScriptResults;

/// <summary>
/// The saved views of the current workspace's results windows, in <c>.vs\EquilibriumDamlTools\view-state.json</c>
/// under the open folder or solution. Read on first use, written shortly after a change, both
/// off the UI thread. Without a workspace views are kept for the session only.
/// </summary>
internal sealed class ViewStateStore : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly IDamlLogger _logger;
    private readonly Debouncer _saves = new(Quiet, TimeProvider.System);
    // Oldest first, so the file keeps the most recent views when it has to drop some.
    private readonly List<KeyValuePair<string, ScriptResultsViewState>> _views = [];
    private string? _file;
    private bool _loaded;

    public ViewStateStore(IDamlLogger logger) => _logger = logger;

    /// <summary>The saved view of <paramref name="resource"/> in <paramref name="workspace"/>, or the page's default.</summary>
    public async Task<ScriptResultsViewState> GetAsync(string? workspace, string resource)
    {
        var file = workspace is null ? null : Path.Combine(workspace, ".vs", "EquilibriumDamlTools", "view-state.json");
        bool load;
        lock (_gate)
        {
            load = !_loaded || !string.Equals(file, _file, StringComparison.OrdinalIgnoreCase);
        }
        if (load)
        {
            await TaskScheduler.Default;
            var read = file is null ? new Dictionary<string, ScriptResultsViewState>() : Read(file);
            lock (_gate)
            {
                _file = file;
                _loaded = true;
                _views.Clear();
                _views.AddRange(read);
            }
        }
        lock (_gate)
        {
            var index = _views.FindIndex(v => v.Key == resource);
            return index >= 0 ? _views[index].Value : ScriptResultsViewState.Default;
        }
    }

    /// <summary>Remembers the view of <paramref name="resource"/> and saves soon after.</summary>
    public void Set(string resource, ScriptResultsViewState view)
    {
        lock (_gate)
        {
            _views.RemoveAll(v => v.Key == resource);
            _views.Add(new KeyValuePair<string, ScriptResultsViewState>(resource, view));
        }
        SaveAsync().Forget();
    }

    /// <summary>Drops a save still waiting for quiet.</summary>
    public void Dispose() => _saves.Dispose();

    private async Task SaveAsync()
    {
        if (!await _saves.WaitAsync().ConfigureAwait(false))
        {
            return;
        }
        string? file;
        string text;
        lock (_gate)
        {
            file = _file;
            text = ViewStateFile.Write(_views.ToList());
        }
        if (file is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Couldn't save the script results views to {file}: {ex.Message}");
        }
    }

    private IReadOnlyDictionary<string, ScriptResultsViewState> Read(string file)
    {
        try
        {
            return File.Exists(file) ? ViewStateFile.Read(File.ReadAllText(file)) : new Dictionary<string, ScriptResultsViewState>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Couldn't read the script results views from {file}: {ex.Message}");
            return new Dictionary<string, ScriptResultsViewState>();
        }
    }
}
