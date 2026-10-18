using System;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.FileSystem;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Supervision;

namespace Daml.VisualStudio.LanguageServer.Hosting;

/// <summary>
/// Starts and restarts the server the way VS's LSP client needs it. VS restarts a crashed
/// server by itself; every other restart (settings, manifests, the user's Restart) has to go
/// through the client's StopAsync and StartAsync events, and a server that never answered
/// initialize, or stopped answering since, must be killed first, or VS waits for it for about
/// 20 seconds. The workspace is looked up again on each run, so a restart also picks up a moved
/// folder or a new daml.yaml.
/// </summary>
public sealed class ClientRestartSequence : IDisposable
{
    private readonly LanguageServerHost _host;
    private readonly Func<CancellationToken, Task<(WorkspaceContext? Workspace, LanguageServerOptions Options)>> _discover;
    private readonly Func<Task> _raiseStop;
    private readonly Func<Task> _raiseStart;
    private readonly SemaphoreSlim _runs = new(1, 1);
    private readonly CancellationTokenSource _ending = new();
    private int _disposed;
    private bool _clientStarted;

    /// <summary>Creates the sequence.</summary>
    /// <param name="host">The host behind the client.</param>
    /// <param name="discover">Finds the current workspace (or none) and options.</param>
    /// <param name="raiseStop">Raises the client's StopAsync event.</param>
    /// <param name="raiseStart">Raises the client's StartAsync event, after which VS activates it.</param>
    public ClientRestartSequence(
        LanguageServerHost host,
        Func<CancellationToken, Task<(WorkspaceContext? Workspace, LanguageServerOptions Options)>> discover,
        Func<Task> raiseStop,
        Func<Task> raiseStart)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _raiseStop = raiseStop ?? throw new ArgumentNullException(nameof(raiseStop));
        _raiseStart = raiseStart ?? throw new ArgumentNullException(nameof(raiseStart));
        _host.Supervisor.RestartRequested += OnRestartRequested;
    }

    /// <summary>Raised after every restart run, including ones the supervisor asked for.</summary>
    public event EventHandler? RestartCompleted;

    /// <summary>First start, when the client is loaded.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => RunAsync(restart: false, cancellationToken);

    /// <summary>Restart (or first start, if there was no workspace before).</summary>
    public Task RestartAsync(CancellationToken cancellationToken) => RunAsync(restart: true, cancellationToken);

    /// <summary>
    /// Another folder or solution opened: serve it if it has a Daml workspace, otherwise stop
    /// the old server without telling the user there's no daml.yaml.
    /// </summary>
    public Task FolderChangedAsync(CancellationToken cancellationToken) => RunAsync(restart: true, cancellationToken, quiet: true);

    /// <summary>Stops listening for restart requests.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _host.Supervisor.RestartRequested -= OnRestartRequested;
        _ending.Cancel();
        // Runs that already entered still release _runs and may link to _ending. Their managed
        // synchronization objects are collected after the last participant leaves.
    }

    private async Task RunAsync(bool restart, CancellationToken cancellationToken, bool quiet = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ClientRestartSequence));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ending.Token);
        cancellationToken = linked.Token;
        await _runs.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var served = _host.Workspace;
            var (workspace, options) = await _discover(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _host.SetWorkspace(workspace, options);

            // Visual Studio activates the server for a newly opened folder by itself; when that
            // is the workspace already being served, with the same Daml projects of the solution,
            // there is nothing to restart. A project outside the folder added or removed is.
            if (quiet && _clientStarted && workspace is not null && served is not null
                && DamlPath.Comparer.Equals(workspace.WorkspaceRoot, served.WorkspaceRoot)
                && DamlPath.Comparer.Equals(workspace.ServerWorkingDirectory, served.ServerWorkingDirectory)
                && served.IsFor(workspace.SolutionPackages))
            {
                return;
            }

            if (restart && _clientStarted)
            {
                if (_host.Supervisor.State != LanguageServerState.Ready || _host.IsUnresponsive)
                {
                    await _host.StopAsync(cancellationToken, immediately: true).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _host.Log(DamlLogLevel.Information, "Stopping the language client for the restart.");
                // The client's stop ends the server, maybe before the host's own stop below.
                _host.Supervisor.ExpectExit();
                await _raiseStop().ConfigureAwait(false);
                await _host.StopAsync(cancellationToken).ConfigureAwait(false);
                _clientStarted = false;
            }

            if (workspace is null)
            {
                if (!quiet)
                {
                    _host.ReportNoWorkspace();
                }
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (restart)
            {
                _host.Log(DamlLogLevel.Information, "Starting the language client again.");
            }
            await _raiseStart().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _clientStarted = true;
        }
        finally
        {
            _runs.Release();
            if (restart && Volatile.Read(ref _disposed) == 0)
            {
                RestartCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnRestartRequested(object? sender, RestartRequestedEventArgs e) =>
        _ = RestartFromRequestAsync(string.Join("; ", e.Reasons));

    private async Task RestartFromRequestAsync(string reasons)
    {
        try
        {
            _host.Log(DamlLogLevel.Information, $"Restarting the language server: {reasons}.");
            await RestartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
        {
            // The extension is closing; cancellation is the expected end of a restart.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log(DamlLogLevel.Error, $"Restarting the language server ({reasons}) failed.", ex);
        }
        finally
        {
            // Also when the run started nothing (no workspace yet): the next reason must be
            // heard, or the user's own Restart would be silently queued behind this one.
            if (Volatile.Read(ref _disposed) == 0)
            {
                _host.Supervisor.AcknowledgeRestart();
            }
        }
    }
}
