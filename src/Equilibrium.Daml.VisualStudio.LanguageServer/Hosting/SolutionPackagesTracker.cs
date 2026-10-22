using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;

/// <summary>
/// Restarts the server when the solution's Daml projects change: one is added, removed or renamed.
/// A solution loads its projects one by one, so the projects are read once things have been quiet,
/// and the server is restarted only when the packages it would serve differ from those it serves.
/// </summary>
public sealed class SolutionPackagesTracker : IDisposable
{
    /// <summary>How long project events have to stop before the projects are read.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    private readonly WorkspaceDiscovery _discovery;
    private readonly Func<Task<IReadOnlyList<string>>> _readManifests;
    private readonly Func<WorkspaceContext?> _current;
    private readonly Func<Task> _restart;
    private readonly Debouncer _quiet;

    /// <summary>Creates the tracker.</summary>
    /// <param name="discovery">Turns manifests into the packages they stand for.</param>
    /// <param name="readManifests">Reads the daml.yaml of each Daml project in the solution.</param>
    /// <param name="current">The context the server runs with, if any.</param>
    /// <param name="restart">Restarts the server, looking the workspace up again.</param>
    /// <param name="time">The clock the quiet period runs on.</param>
    public SolutionPackagesTracker(
        WorkspaceDiscovery discovery,
        Func<Task<IReadOnlyList<string>>> readManifests,
        Func<WorkspaceContext?> current,
        Func<Task> restart,
        TimeProvider time)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _readManifests = readManifests ?? throw new ArgumentNullException(nameof(readManifests));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _quiet = new Debouncer(Quiet, time ?? throw new ArgumentNullException(nameof(time)));
    }

    /// <summary>A project was added, removed or renamed; the last of a burst does the work.</summary>
    public async Task OnProjectsChangedAsync()
    {
        if (!await _quiet.WaitAsync().ConfigureAwait(false))
        {
            return;
        }
        var packages = _discovery.PackagesOf(await _readManifests().ConfigureAwait(false));
        var unchanged = _current() is { } workspace ? workspace.IsFor(packages) : packages.Count == 0;
        if (!unchanged)
        {
            await _restart().ConfigureAwait(false);
        }
    }

    /// <summary>Drops a read still waiting for quiet.</summary>
    public void Dispose() => _quiet.Dispose();
}
