using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Protocol;
using Daml.VisualStudio.LanguageServer.Supervision;
using StreamJsonRpc;

namespace Daml.VisualStudio.LanguageServer.Hosting;

/// <summary>What keeps the language server from running, for the user to see.</summary>
public enum LanguageServerProblemKind
{
    /// <summary>No daml.yaml for the open folder or file: language features are off.</summary>
    NoWorkspace,

    /// <summary>dpm, the daml assistant or the SDK is missing; the message says what to do.</summary>
    ToolMissing,

    /// <summary>The server can't run this workspace for another reason given in the message.</summary>
    CannotStart,

    /// <summary>The server crashed too often and won't be restarted until the user asks.</summary>
    CircuitOpen,

    /// <summary>The server stopped answering and is being restarted.</summary>
    Unresponsive,
}

/// <summary>A problem worth telling the user about.</summary>
/// <param name="Kind">What kind of problem.</param>
/// <param name="Message">Text for the user.</param>
public sealed record LanguageServerProblem(LanguageServerProblemKind Kind, string Message);

/// <summary>Carries a <see cref="LanguageServerProblem"/>.</summary>
public sealed class LanguageServerProblemEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public LanguageServerProblemEventArgs(LanguageServerProblem problem) => Problem = problem;

    /// <summary>The problem.</summary>
    public LanguageServerProblem Problem { get; }
}

/// <summary>A session that answered <c>initialize</c> and whose connection is attached.</summary>
public sealed class SessionReadyEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public SessionReadyEventArgs(int sessionId) => SessionId = sessionId;

    /// <summary>The session.</summary>
    public int SessionId { get; }
}

/// <summary>The streams VS's LSP client connects to, for one session.</summary>
/// <param name="SessionId">The session they belong to.</param>
/// <param name="FromServer">What the client reads.</param>
/// <param name="ToServer">What the client writes.</param>
public sealed record ServerConnection(int SessionId, Stream FromServer, Stream ToServer);

/// <summary>
/// Everything behind the VS language client that doesn't need VS: picks the tool and command for
/// the current workspace, starts the server through the supervisor, puts the protocol adapter
/// between VS and damlc, and decides when a file change needs a restart. The VS client only
/// forwards its calls here and shows the problems this raises.
/// </summary>
public sealed class LanguageServerHost : IDisposable
{
    private readonly SdkResolver _sdkResolver;
    private readonly IDamlLogger _logger;
    private readonly LspAdapterSettings _lspSettings;
    private readonly DamlRpcChannel _channel = new();
    private readonly object _gate = new();
    private WorkspaceContext? _workspace;
    private LanguageServerOptions _options = new();
    private SdkResolution? _lastResolution;
    private int _currentSession;
    private int _initializedSession;
    private int _attachedSession;
    private int _unresponsiveSession;
    private bool _ready;
    private int _activations;

    /// <summary>Creates a host.</summary>
    public LanguageServerHost(
        IProcessRunner runner,
        SdkResolver sdkResolver,
        IDamlLogger logger,
        TimeProvider? time = null,
        RestartPolicy? policy = null,
        SupervisorSettings? supervisorSettings = null,
        LspAdapterSettings? lspSettings = null,
        KeepAliveSettings? keepAlive = null)
    {
        _sdkResolver = sdkResolver ?? throw new ArgumentNullException(nameof(sdkResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lspSettings = lspSettings ?? new LspAdapterSettings();
        Supervisor = new LanguageServerSupervisor(runner, BuildCommandAsync, logger, time, policy, supervisorSettings);
        Supervisor.StateChanged += OnStateChanged;
        KeepAlive = new KeepAliveWatchdog(_channel, OnUnresponsive, logger, time, keepAlive);
    }

    /// <summary>Something keeps the server from running.</summary>
    public event EventHandler<LanguageServerProblemEventArgs>? ProblemRaised;

    /// <summary>A session answered <c>initialize</c> and its connection is attached: dependent services may start.</summary>
    public event EventHandler<SessionReadyEventArgs>? SessionReady;

    /// <summary><see cref="IsReady"/> changed.</summary>
    public event EventHandler? ReadinessChanged;

    /// <summary>
    /// Whether a session is serving right now: true from the moment it is ready until the
    /// server leaves the Ready state (stopping, crashing, restarting).
    /// </summary>
    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    /// <summary>Process ownership, backoff and restarts.</summary>
    public LanguageServerSupervisor Supervisor { get; }

    /// <summary>Whether the current session stopped answering; such a server won't answer a shutdown either.</summary>
    public bool IsUnresponsive
    {
        get
        {
            lock (_gate)
            {
                return _unresponsiveSession != 0 && _unresponsiveSession == _currentSession;
            }
        }
    }

    internal KeepAliveWatchdog KeepAlive { get; }

    /// <summary>
    /// Whether, and how often, the server is checked for hangs. Takes effect at once: turning it
    /// off stops the checks, turning it on starts them for a ready session.
    /// </summary>
    public KeepAliveSettings KeepAliveSettings
    {
        get => KeepAlive.Settings;
        set
        {
            KeepAlive.Settings = value ?? throw new ArgumentNullException(nameof(value));
            int ready;
            lock (_gate)
            {
                ready = _currentSession != 0 && _initializedSession == _currentSession && _attachedSession == _currentSession && _unresponsiveSession != _currentSession
                    ? _currentSession
                    : 0;
            }
            if (!value.Enabled || ready == 0 || Supervisor.State != LanguageServerState.Ready)
            {
                KeepAlive.Stop();
                return;
            }
            KeepAlive.Watch(ready);
        }
    }

    /// <summary>The current session's JSON-RPC connection.</summary>
    public IDamlRpcChannel Channel => _channel;

    /// <summary>The tool and SDK versions of the latest start, for status display.</summary>
    public SdkResolution? LastResolution
    {
        get
        {
            lock (_gate)
            {
                return _lastResolution;
            }
        }
    }

    /// <summary>The workspace the next (or current) session serves; <c>null</c> when there is none.</summary>
    public WorkspaceContext? Workspace
    {
        get
        {
            lock (_gate)
            {
                return _workspace;
            }
        }
    }

    /// <summary>Sets what the next start runs: the workspace (<c>null</c> when there is no daml.yaml) and the options.</summary>
    public void SetWorkspace(WorkspaceContext? workspace, LanguageServerOptions options)
    {
        lock (_gate)
        {
            _workspace = workspace;
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }
    }

    /// <summary>
    /// Starts a session and returns the streams for VS's connection, or <c>null</c> (after raising
    /// <see cref="ProblemRaised"/>) when the server can't or mustn't start.
    /// </summary>
    public async Task<ServerConnection?> ActivateAsync(CancellationToken cancellationToken)
    {
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"Visual Studio asked for a server session (activation {Interlocked.Increment(ref _activations)}).");
        lock (_gate)
        {
            if (_workspace is null)
            {
                Raise(LanguageServerProblemKind.NoWorkspace, "No daml.yaml found for the open folder or file; Daml language features are off.");
                return null;
            }
        }

        LanguageServerSession? session;
        try
        {
            session = await Supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SdkResolutionException ex)
        {
            Raise(LanguageServerProblemKind.ToolMissing, $"{ex.Message} ({ex.Code})");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Raise(LanguageServerProblemKind.CannotStart, ex.Message);
            return null;
        }
        if (session is null)
        {
            Raise(LanguageServerProblemKind.CircuitOpen, "The Daml language server keeps crashing and was stopped. Use Restart to try again; the Output window has its log.");
            return null;
        }

        lock (_gate)
        {
            _currentSession = session.Id;
        }
        var tracker = new InitializeTracker();
        var id = session.Id;
        tracker.Responded += (_, _) => OnInitialized(id);
        tracker.Failed += (_, e) => _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, $"The language server refused to initialize (session {id}): {e.Message}");
        // Per session: damlc of a new session has never seen any document.
        var documents = new OpenDocuments();
        return new ServerConnection(
            session.Id,
            new ServerToClientStream(session.Output, _lspSettings, tracker, _logger, documents),
            new ClientToServerStream(session.Input, _lspSettings, tracker, _logger, documents));
    }

    /// <summary>
    /// Whether messages about <paramref name="documentUri"/> may go to the server. VS sends every
    /// open Daml document to the client, but damlc multi-ide stops altogether when it gets a file
    /// with no daml.yaml above it, so files outside the workspace are kept away (they still get
    /// syntax colouring). Other URI schemes, such as the server's own virtual documents, pass.
    /// </summary>
    public bool IsServed(string? documentUri)
    {
        // Some clients escape the drive colon (file:///m%3A/...), which Uri leaves in LocalPath.
        var unescapedDrive = documentUri?.Replace("%3A", ":").Replace("%3a", ":");
        if (unescapedDrive is null || !Uri.TryCreate(unescapedDrive, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return true;
        }
        WorkspaceContext? workspace;
        lock (_gate)
        {
            workspace = _workspace;
        }
        return workspace is not null && workspace.Contains(uri.LocalPath);
    }

    /// <summary>VS handed over the JSON-RPC connection of the session it just activated.</summary>
    public void AttachRpc(JsonRpc rpc)
    {
        int session;
        lock (_gate)
        {
            session = _currentSession;
            _attachedSession = session;
        }
        _channel.Attach(rpc, session);
        RaiseReadyIfComplete(session);
    }

    /// <summary>Ends the current session (VS has sent shutdown/exit or is about to drop the connection).</summary>
    /// <param name="cancellationToken">Stops waiting; the session is still ended.</param>
    /// <param name="immediately">Kill the process tree at once instead of waiting for it to leave.</param>
    public Task StopAsync(CancellationToken cancellationToken, bool immediately = false) =>
        Supervisor.StopAsync(cancellationToken, immediately ? TimeSpan.Zero : null);

    /// <summary>Tells the user that there's no daml.yaml to serve.</summary>
    public void ReportNoWorkspace() =>
        Raise(LanguageServerProblemKind.NoWorkspace, "No daml.yaml found for the open folder or file; Daml language features are off.");

    /// <summary>Writes to the language server log.</summary>
    public void Log(DamlLogLevel level, string message, Exception? exception = null) =>
        _logger.Log(DamlLogCategory.LanguageServer, level, message, exception);

    /// <summary>
    /// A file in the workspace changed. The single-package server doesn't notice manifest edits
    /// and the server's folder may depend on multi-package.yaml, so those ask for a restart; the
    /// multi-package server picks up daml.yaml edits by itself. With no workspace yet, a new
    /// daml.yaml is a reason to try again.
    /// </summary>
    public void OnFileChanged(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        var name = Path.GetFileName(path);
        var isPackageManifest = name.Equals(WorkspaceLocator.PackageManifestName, StringComparison.OrdinalIgnoreCase);
        var isMultiPackageManifest = name.Equals(WorkspaceLocator.MultiPackageManifestName, StringComparison.OrdinalIgnoreCase);

        WorkspaceContext? workspace;
        lock (_gate)
        {
            workspace = _workspace;
        }
        if (workspace is null)
        {
            if (isPackageManifest || isMultiPackageManifest)
            {
                Supervisor.RequestRestart($"{name} appeared");
            }
            return;
        }
        if (!workspace.IsInvalidatedBy(path))
        {
            return;
        }
        if (isMultiPackageManifest || workspace.MultiPackageManifestPath is null)
        {
            Supervisor.RequestRestart($"{name} changed");
        }
    }

    /// <summary>Ends the server process tree.</summary>
    public void Dispose()
    {
        Supervisor.StateChanged -= OnStateChanged;
        KeepAlive.Dispose();
        Supervisor.Dispose();
    }

    private async Task<ProcessSpec> BuildCommandAsync(CancellationToken cancellationToken)
    {
        WorkspaceContext workspace;
        LanguageServerOptions options;
        lock (_gate)
        {
            workspace = _workspace ?? throw new InvalidOperationException("There is no Daml workspace to serve.");
            options = _options;
        }

        var sdk = await _sdkResolver.ResolveAsync(workspace, options, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _lastResolution = sdk;
        }
        foreach (var note in sdk.Notes)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, note);
        }
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information,
            $"Using {sdk.Assistant.Path}; damlc {sdk.CoordinatorVersion ?? "(version unknown)"}, workspace SDK {sdk.ContextSdkVersion ?? "(none pinned)"}.");
        return LanguageServerCommandBuilder.Build(sdk, workspace, options);
    }

    private void OnInitialized(int session)
    {
        Supervisor.MarkReady(session);
        lock (_gate)
        {
            _initializedSession = session;
        }
        RaiseReadyIfComplete(session);
    }

    private void RaiseReadyIfComplete(int session)
    {
        lock (_gate)
        {
            if (session != _currentSession || _initializedSession != session || _attachedSession != session)
            {
                return;
            }
        }
        KeepAlive.Watch(session);
        SetReady(true);
        SessionReady?.Invoke(this, new SessionReadyEventArgs(session));
    }

    // The watchdog's report; one about a session that is already gone changes nothing.
    internal void OnUnresponsive(int session)
    {
        lock (_gate)
        {
            if (session == 0 || session != _currentSession)
            {
                return;
            }
            _unresponsiveSession = session;
        }
        Raise(LanguageServerProblemKind.Unresponsive, "The Daml language server stopped responding and was restarted.");
        Supervisor.RequestRestart("The language server stopped responding");
    }

    private void OnStateChanged(object? sender, LanguageServerStateChangedEventArgs e)
    {
        if (e.NewState != LanguageServerState.Ready)
        {
            SetReady(false);
        }
        if (e.NewState is LanguageServerState.Crashed or LanguageServerState.Stopping or LanguageServerState.Stopped or LanguageServerState.Faulted)
        {
            int session;
            lock (_gate)
            {
                session = _currentSession;
            }
            KeepAlive.Stop();
            _channel.Detach(session);
        }
    }

    private void SetReady(bool ready)
    {
        lock (_gate)
        {
            if (_ready == ready)
            {
                return;
            }
            _ready = ready;
        }
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Raise(LanguageServerProblemKind kind, string message)
    {
        _logger.Log(DamlLogCategory.LanguageServer, kind == LanguageServerProblemKind.NoWorkspace ? DamlLogLevel.Information : DamlLogLevel.Error, message);
        ProblemRaised?.Invoke(this, new LanguageServerProblemEventArgs(new LanguageServerProblem(kind, message)));
    }
}
