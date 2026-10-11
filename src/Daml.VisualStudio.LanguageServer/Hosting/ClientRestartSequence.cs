using System;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Supervision;

namespace Daml.VisualStudio.LanguageServer.Hosting;

/// <summary>
/// Starts and restarts the server the way VS's LSP client needs it. VS restarts a crashed
/// server by itself; every other restart (settings, manifests, the user's Restart) has to go
/// through the client's StopAsync and StartAsync events, and a server that never answered
/// initialize must be killed first, or VS waits for it for about 20 seconds. The workspace is
/// looked up again on each run, so a restart also picks up a moved folder or a new daml.yaml.
/// </summary>
public sealed class ClientRestartSequence : IDisposable
{
    private readonly LanguageServerHost _host;
    private readonly Func<CancellationToken, Task<(WorkspaceContext? Workspace, LanguageServerOptions Options)>> _discover;
    private readonly Func<Task> _raiseStop;
    private readonly Func<Task> _raiseStart;
    private readonly SemaphoreSlim _runs = new(1, 1);
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

    /// <summary>Stops listening for restart requests.</summary>
    public void Dispose()
    {
        _host.Supervisor.RestartRequested -= OnRestartRequested;
        _runs.Dispose();
    }

    private async Task RunAsync(bool restart, CancellationToken cancellationToken)
    {
        await _runs.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (workspace, options) = await _discover(cancellationToken).ConfigureAwait(false);
            _host.SetWorkspace(workspace, options);

            if (restart && _clientStarted)
            {
                if (_host.Supervisor.State != LanguageServerState.Ready)
                {
                    await _host.StopAsync(cancellationToken, immediately: true).ConfigureAwait(false);
                }
                await _raiseStop().ConfigureAwait(false);
                await _host.StopAsync(cancellationToken).ConfigureAwait(false);
                _clientStarted = false;
            }

            if (workspace is null)
            {
                _host.ReportNoWorkspace();
                return;
            }
            await _raiseStart().ConfigureAwait(false);
            _clientStarted = true;
        }
        finally
        {
            _runs.Release();
            if (restart)
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
            await RestartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log(DamlLogLevel.Error, $"Restarting the language server ({reasons}) failed.", ex);
        }
    }
}
