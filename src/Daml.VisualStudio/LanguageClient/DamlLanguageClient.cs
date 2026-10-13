using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.FileSystem;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Options;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Messages;
using Daml.VisualStudio.LanguageServer.Protocol;
using Daml.VisualStudio.Options;
using Microsoft.VisualStudio.Imaging;
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
/// <see cref="LanguageServerHost"/> and shows what the host reports; deciding what to start,
/// when to restart and how to talk to damlc happens there.
/// </summary>
[Export(typeof(ILanguageClient))]
[Export(typeof(DamlLanguageClient))]
[ContentType(DamlContentDefinition.ContentTypeName)]
internal sealed class DamlLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
{
    private const string RestartAction = "Restart";
    private const string ApplySettingsAction = "Restart now";

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;
    private readonly LanguageServerHost _host;
    private readonly ClientRestartSequence _sequence;
    private readonly WorkspaceDiscovery _discovery;
    private readonly DamlSettingsMonitor _settings = new();
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _folderWatcher;
    private string? _watchedRoot;
    private int _version;
    private string? _openedFolder;

    [ImportingConstructor]
    public DamlLanguageClient(JoinableTaskContext joinableTaskContext, IDamlLogger logger)
    {
        _joinableTaskFactory = joinableTaskContext.Factory;
        _logger = logger;
        var files = new PhysicalFileSystem();
        var manifests = new YamlManifestReader(files);
        var runner = new ProcessRunner();
        _discovery = new WorkspaceDiscovery(files, manifests);
        _host = new LanguageServerHost(runner, new SdkResolver(runner, new AssistantLocator(), manifests), logger);
        _host.ProblemRaised += OnProblemRaised;
        _host.ReadinessChanged += OnReadinessChanged;
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
        MiddleLayer = new DamlMiddleLayer(logger, _host.IsServed, new LongPathRedirector(libraryCopies, new LibraryCopier()).Redirect);
        var installs = new SdkInstallProgressTracker(
            new TaskStatusCenterPresenter(_joinableTaskFactory, logger),
            id => _host.Channel.CancelSdkInstallAsync(id),
            logger);
        CustomMessageTarget = new DamlCustomMessageTarget(new LoggingVirtualResourceSink(logger), installs, logger);
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
        // Like any status bar text, the next message from Visual Studio replaces it.
        _joinableTaskFactory.RunAsync(() => VS.StatusBar.ShowMessageAsync("Daml: starting…")).FileAndForget("daml/languageClient/starting");
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
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _folderWatcher?.Dispose();
            _folderWatcher = null;
        }
        _settings.Dispose();
        VS.Events.SolutionEvents.OnAfterOpenFolder -= OnFolderOpened;
        VS.Events.SolutionEvents.OnAfterOpenSolution -= OnSolutionOpened;
        VS.Events.SolutionEvents.OnAfterCloseFolder -= OnFolderClosed;
        VS.Events.SolutionEvents.OnAfterCloseSolution -= OnWorkspaceClosed;
        _sequence.Dispose();
        _host.Dispose(); // closes the job, so the server can't outlive VS
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
        Watch(workspace?.WorkspaceRoot ?? root);
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
            _joinableTaskFactory.RunAsync(() => ShowRestartRequiredAsync(e.ChangesNeedingRestart)).FileAndForget("daml/options/restartRequired");
        }
    }

    private async Task ShowRestartRequiredAsync(IReadOnlyList<string> changes)
    {
        var model = new InfoBarModel(
            new[] { new InfoBarTextSpan($"Daml settings changed ({string.Join(", ", changes)}). Restart the language server to apply them. ") },
            new[] { new InfoBarHyperlink(ApplySettingsAction) },
            KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);
        var infoBar = await VS.InfoBar.CreateAsync(model);
        if (infoBar is null)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, "Couldn't show the restart bar for the changed settings; use Restart to apply them.");
            return;
        }
        infoBar.ActionItemClicked += (_, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // VS raises it from the info bar's UI
            if (args.ActionItem.Text == ApplySettingsAction)
            {
                args.InfoBarUIElement.Close();
                _host.Supervisor.RequestRestart("Options changed");
            }
        };
        if (!await infoBar.TryShowInfoBarUIAsync())
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, "Couldn't show the restart bar for the changed settings; use Restart to apply them.");
        }
    }

    private const int WatcherBufferSize = 64 * 1024;

    // Manifest changes the server won't notice by itself go to the host, which may ask for a restart.
    private void Watch(string? root)
    {
        lock (_gate)
        {
            if (root is null || string.Equals(root, _watchedRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            {
                return;
            }
            _watcher?.Dispose();
            _watchedRoot = root;
            // One filter only on .NET Framework; the host ignores YAML files that aren't manifests.
            // A build or clean touches thousands of entries under .daml at once, more than the
            // default 8 KB buffer holds; when events are lost anyway, the host is told.
            _watcher = new FileSystemWatcher(root, "*.yaml") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, InternalBufferSize = WatcherBufferSize };
            _watcher.Changed += (_, e) => _host.OnFileChanged(e.FullPath);
            _watcher.Created += (_, e) => _host.OnFileChanged(e.FullPath);
            _watcher.Deleted += (_, e) => _host.OnFileChanged(e.FullPath);
            _watcher.Renamed += (_, e) => _host.OnFileChanged(e.FullPath);
            _watcher.Error += (_, _) => _host.OnWatcherOverflow();
            _watcher.EnableRaisingEvents = true;

            // dpm clean deletes the package database damlc reads library sources from.
            _folderWatcher?.Dispose();
            _folderWatcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName, InternalBufferSize = WatcherBufferSize };
            _folderWatcher.Deleted += (_, e) => _host.OnDirectoryRemoved(e.FullPath);
            _folderWatcher.Error += (_, _) => _host.OnWatcherOverflow();
            _folderWatcher.EnableRaisingEvents = true;
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
    // next folder opens.
    private void OnWorkspaceClosed()
    {
        Volatile.Write(ref _openedFolder, null);
        OnWorkspaceOpened();
    }

    private void OnWorkspaceOpened() =>
        _joinableTaskFactory.RunAsync(() => _sequence.FolderChangedAsync(CancellationToken.None)).FileAndForget("daml/languageClient/folderChanged");

    private void OnReadinessChanged(object? sender, EventArgs e) =>
        _joinableTaskFactory.RunAsync(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            // Read again on the UI thread, so a quick ready-then-stopped can't leave it stale.
            var ready = _host.IsReady;
            UIContext.FromUIContextGuid(PackageGuids.LanguageServerReady).IsActive = ready;
            if (ready)
            {
                await VS.StatusBar.ShowMessageAsync("Daml: ready");
            }
        }).FileAndForget("daml/languageClient/readiness");

    private void OnProblemRaised(object? sender, LanguageServerProblemEventArgs e) =>
        _joinableTaskFactory.RunAsync(() => ShowProblemAsync(e.Problem)).FileAndForget("daml/languageClient/showProblem");

    private async Task ShowProblemAsync(LanguageServerProblem problem)
    {
        var actions = problem.Kind == LanguageServerProblemKind.CircuitOpen
            ? new[] { new InfoBarHyperlink(RestartAction) }
            : Array.Empty<InfoBarHyperlink>();
        var model = new InfoBarModel(
            new[] { new InfoBarTextSpan(problem.Message + " ") },
            actions,
            problem.Kind == LanguageServerProblemKind.NoWorkspace ? KnownMonikers.StatusInformation : KnownMonikers.StatusWarning,
            isCloseButtonVisible: true);
        var infoBar = await VS.InfoBar.CreateAsync(model);
        if (infoBar is null)
        {
            return;
        }
        infoBar.ActionItemClicked += (_, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // VS raises it from the info bar's UI
            if (args.ActionItem.Text == RestartAction)
            {
                args.InfoBarUIElement.Close();
                _host.Supervisor.ResetCircuit();
            }
        };
        await infoBar.TryShowInfoBarUIAsync();
    }
}
