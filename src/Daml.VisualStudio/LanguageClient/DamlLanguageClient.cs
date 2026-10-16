using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Manifests;
using Daml.Build.Core.Processes;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Options;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Messages;
using Daml.VisualStudio.LanguageServer.Protocol;
using Daml.VisualStudio.LanguageServer.VirtualResources;
using Daml.VisualStudio.Options;
using Daml.VisualStudio.ScriptResults;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using StreamJsonRpc;
using Task = System.Threading.Tasks.Task;

namespace Daml.VisualStudio.LanguageClient;

/// <summary>
/// VS's language client for Daml: one instance, one damlc process. It only adapts VS's calls to
/// <see cref="LanguageServerHost"/>; deciding what to start, when to restart and how to talk to
/// damlc happens there, watching the folder is <see cref="WorkspaceWatcher"/>'s job and telling
/// the user is <see cref="LanguageServerNotifier"/>'s.
/// </summary>
[Export(typeof(ILanguageClient))]
[Export(typeof(DamlLanguageClient))]
[ContentType(DamlContentDefinition.ContentTypeName)]
internal sealed class DamlLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
{
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;
    private readonly LanguageServerHost _host;
    private readonly ClientRestartSequence _sequence;
    private readonly WorkspaceDiscovery _discovery;
    private readonly WorkspaceWatcher _watcher;
    private readonly LanguageServerNotifier _notifier;
    private readonly ScriptResultsWindows _scriptResults;
    private readonly DamlSettingsMonitor _settings = new();
    private int _version;
    private string? _openedFolder;

    [ImportingConstructor]
    public DamlLanguageClient(JoinableTaskContext joinableTaskContext, IDamlLogger logger, ScriptResultsWindows scriptResults)
    {
        _joinableTaskFactory = joinableTaskContext.Factory;
        _logger = logger;
        _scriptResults = scriptResults;
        var files = new PhysicalFileSystem();
        var manifests = new YamlManifestReader(files);
        var runner = new ProcessRunner();
        _discovery = new WorkspaceDiscovery(files, manifests);
        _host = new LanguageServerHost(runner, new SdkResolver(runner, new AssistantLocator(), manifests), logger);
        _watcher = new WorkspaceWatcher(_host);
        _notifier = new LanguageServerNotifier(_joinableTaskFactory, logger, _host);
        _host.ProblemRaised += _notifier.OnProblemRaised;
        _host.ReadinessChanged += _notifier.OnReadinessChanged;
        // Solution events have to be subscribed on the UI thread; nothing waits for it.
        _joinableTaskFactory.RunAsync(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            VS.Events.SolutionEvents.OnAfterOpenFolder += OnFolderOpened;
            VS.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionOpened;
            VS.Events.SolutionEvents.OnAfterCloseFolder += OnFolderClosed;
            VS.Events.SolutionEvents.OnAfterCloseSolution += OnWorkspaceClosed;
        }).FileAndForget("daml/languageClient/solutionEvents");
        _settings.Changed += OnSettingsChanged;
        // Library sources sit under paths longer than Visual Studio opens; F12 goes to short copies.
        var libraryCopies = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Daml", "VisualStudio", "library");
        var libraryPaths = new LongPathRedirector(libraryCopies, new LibraryCopier());
        MiddleLayer = new DamlMiddleLayer(logger, _host.IsServed, libraryPaths.Redirect, libraryPaths.OriginalDocument);
        var installs = new SdkInstallProgressTracker(
            new TaskStatusCenterPresenter(_joinableTaskFactory, logger),
            id => _host.Channel.CancelSdkInstallAsync(id),
            logger);
        VirtualResources = new VirtualResourceManager(_host.Channel, scriptResults, logger);
        _host.SessionReady += (_, e) => VirtualResources.OnSessionReady(e.SessionId);
        CustomMessageTarget = new DamlCustomMessageTarget(VirtualResources, installs, logger);
        _sequence = new ClientRestartSequence(_host, DiscoverAsync, () => RaiseAsync(StopAsync), () => RaiseAsync(StartAsync));
    }

    public event AsyncEventHandler<EventArgs>? StartAsync;

    public event AsyncEventHandler<EventArgs>? StopAsync;

    // VS names an Output pane after the client, so it must not clash with our panes ("Daml",
    // "Daml Language Server", "Daml Build").
    public string Name => "Daml Language Client";

    public IEnumerable<string> ConfigurationSections { get; } = ["daml"];

    public object? InitializationOptions => null;

    // Dynamic registration from the server is accepted by VS but never acted on, so the
    // patterns damlc asks for are declared here.
    public IEnumerable<string> FilesToWatch { get; } = ["**/multi-package.yaml", "**/daml.yaml", "**/*.dar", "**/*.daml"];

    public bool ShowNotificationOnInitializeFailed => true;

    public object MiddleLayer { get; }

    // Receives damlc's own daml/* notifications.
    public object CustomMessageTarget { get; }

    /// <summary>Script results: what a lens or a command opens.</summary>
    public VirtualResourceManager VirtualResources { get; }

    /// <summary>The host behind this client, for commands such as Restart.</summary>
    public LanguageServerHost Host => _host;

    public Task OnLoadedAsync() => _sequence.StartAsync(CancellationToken.None);

    public async Task<Connection?> ActivateAsync(CancellationToken token)
    {
        var connection = await _host.ActivateAsync(token).ConfigureAwait(false);
        if (connection is null)
        {
            return null;
        }
        _notifier.ShowStarting();
        return new Connection(connection.FromServer, connection.ToServer);
    }

    public Task AttachForCustomMessageAsync(JsonRpc rpc)
    {
        _host.AttachRpc(rpc);
        return Task.CompletedTask;
    }

    public Task OnServerInitializedAsync() => Task.CompletedTask;

    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
    {
        var reason = initializationState.InitializationException?.Message ?? initializationState.StatusMessage ?? "unknown reason";
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, $"The Daml language server failed to start: {reason}", initializationState.InitializationException);
        return Task.FromResult<InitializationFailureContext?>(new InitializationFailureContext
        {
            FailureMessage = $"The Daml language server failed to start: {reason}. See the Daml Language Server pane of the Output window.",
        });
    }

    public void Dispose()
    {
        // Solution events are a UI-thread object; MEF may dispose this part from any thread, and
        // the host below must be disposed whatever happens here, or the server outlives VS.
        if (ThreadHelper.CheckAccess())
        {
            UnsubscribeSolutionEvents();
        }
        else
        {
            _joinableTaskFactory.RunAsync(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                UnsubscribeSolutionEvents();
            }).FileAndForget("daml/languageClient/unsubscribe");
        }
        _watcher.Dispose();
        _settings.Dispose();
        _sequence.Dispose();
        VirtualResources.Dispose();
        _host.Dispose(); // closes the job, so the server can't outlive VS
    }

    private void UnsubscribeSolutionEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VS.Events.SolutionEvents.OnAfterOpenFolder -= OnFolderOpened;
        VS.Events.SolutionEvents.OnAfterOpenSolution -= OnSolutionOpened;
        VS.Events.SolutionEvents.OnAfterCloseFolder -= OnFolderClosed;
        VS.Events.SolutionEvents.OnAfterCloseSolution -= OnWorkspaceClosed;
    }

    private async Task<(WorkspaceContext? Workspace, LanguageServerOptions Options)> DiscoverAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? root = null;
        if (await VS.Services.GetSolutionAsync() is IVsSolution solution
            && solution.GetSolutionInfo(out var directory, out _, out _) == 0
            && !string.IsNullOrEmpty(directory))
        {
            root = DamlPath.Normalize(directory);
        }
        // Right after Open Folder, Visual Studio may still report the previous folder or none;
        // the folder from the event is the one being opened.
        root = Volatile.Read(ref _openedFolder) ?? root;
        var view = await VS.Documents.GetActiveDocumentViewAsync();
        var activeFile = view?.FilePath is { } path && path.EndsWith(".daml", StringComparison.OrdinalIgnoreCase) ? path : null;
        var settings = await DamlOptions.GetSettingsAsync();
        await TaskScheduler.Default;

        ApplyKeepAlive(settings);
        var workspace = _discovery.Discover(root, activeFile, Interlocked.Increment(ref _version), settings.ServerMode);
        _watcher.Watch(workspace?.WorkspaceRoot ?? root);
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, workspace is null
            ? $"No Daml workspace in {root ?? "(no folder)"}."
            : $"Daml workspace {workspace.WorkspaceRoot}; server runs in {workspace.ServerWorkingDirectory}.");
        _settings.MarkApplied(settings);
        return (workspace, settings.ToLanguageServerOptions());
    }

    private void ApplyKeepAlive(DamlSettings settings)
    {
        if (_host.KeepAliveSettings.Enabled != settings.KeepAliveEnabled)
        {
            _host.KeepAliveSettings = _host.KeepAliveSettings with { Enabled = settings.KeepAliveEnabled };
        }
    }

    // The keep-alive check follows the page at once; anything that changes the server's command
    // line waits for the user to restart it.
    private void OnSettingsChanged(object? sender, DamlSettingsChangedEventArgs e)
    {
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, e.RestartRequired
            ? $"Daml settings saved; a server restart applies: {string.Join(", ", e.ChangesNeedingRestart)}."
            : "Daml settings saved; nothing needs a server restart.");
        ApplyKeepAlive(e.Settings);
        if (e.RestartRequired)
        {
            _notifier.ShowRestartRequired(e.ChangesNeedingRestart);
        }
    }

    private async Task RaiseAsync(AsyncEventHandler<EventArgs>? handler)
    {
        if (handler is not null)
        {
            await handler.InvokeAsync(this, EventArgs.Empty).ConfigureAwait(false);
        }
    }

    // VS doesn't ask this client for a new session when another folder or solution opens after
    // the last one had no Daml in it, and a running server would keep serving the old folder.
    private void OnFolderOpened(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }
        Volatile.Write(ref _openedFolder, DamlPath.Normalize(folder!));
        OnWorkspaceOpened();
    }

    private void OnSolutionOpened(Solution? solution)
    {
        Volatile.Write(ref _openedFolder, null);
        OnWorkspaceOpened();
    }

    private void OnFolderClosed(string? folder) => OnWorkspaceClosed();

    // Nothing left to serve: the server of the closed folder stops instead of idling until the
    // next folder opens, and the folder's script results go with it.
    private void OnWorkspaceClosed()
    {
        _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, "The folder or solution closed.");
        Volatile.Write(ref _openedFolder, null);
        _joinableTaskFactory.RunAsync(_scriptResults.CloseAllAsync).FileAndForget("daml/scriptResults/closeAll");
        OnWorkspaceOpened();
    }

    private void OnWorkspaceOpened() =>
        _joinableTaskFactory.RunAsync(() => _sequence.FolderChangedAsync(CancellationToken.None)).FileAndForget("daml/languageClient/folderChanged");
}
