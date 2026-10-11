using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.FileSystem;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Hosting;
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
[ContentType(DamlContentDefinition.ContentTypeName)]
internal sealed class DamlLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
{
    private const string RestartAction = "Restart";

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;
    private readonly LanguageServerHost _host;
    private readonly ClientRestartSequence _sequence;
    private readonly WorkspaceDiscovery _discovery;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private string? _watchedRoot;
    private int _version;

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
        MiddleLayer = new DamlMiddleLayer(logger);
        _sequence = new ClientRestartSequence(_host, DiscoverAsync, () => RaiseAsync(StopAsync), () => RaiseAsync(StartAsync));
    }

    public event AsyncEventHandler<EventArgs>? StartAsync;

    public event AsyncEventHandler<EventArgs>? StopAsync;

    public string Name => "Daml Language Server";

    public IEnumerable<string> ConfigurationSections { get; } = ["daml"];

    public object? InitializationOptions => null;

    // Dynamic registration from the server is accepted by VS but never acted on, so the
    // patterns damlc asks for are declared here.
    public IEnumerable<string> FilesToWatch { get; } = ["**/multi-package.yaml", "**/daml.yaml", "**/*.dar", "**/*.daml"];

    public bool ShowNotificationOnInitializeFailed => true;

    public object MiddleLayer { get; }

    // The daml/* notification handlers arrive with the custom message target.
    public object? CustomMessageTarget => null;

    /// <summary>The host behind this client, for commands such as Restart.</summary>
    public LanguageServerHost Host => _host;

    public Task OnLoadedAsync() => _sequence.StartAsync(CancellationToken.None);

    public async Task<Connection?> ActivateAsync(CancellationToken token)
    {
        var connection = await _host.ActivateAsync(token).ConfigureAwait(false);
        return connection is null ? null : new Connection(connection.FromServer, connection.ToServer);
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
        }
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
        var view = await VS.Documents.GetActiveDocumentViewAsync();
        var activeFile = view?.FilePath is { } path && path.EndsWith(".daml", StringComparison.OrdinalIgnoreCase) ? path : null;
        await TaskScheduler.Default;

        var workspace = _discovery.Discover(root, activeFile, Interlocked.Increment(ref _version));
        Watch(workspace?.WorkspaceRoot ?? root);
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, workspace is null
            ? $"No Daml workspace in {root ?? "(no folder)"}."
            : $"Daml workspace {workspace.WorkspaceRoot}; server runs in {workspace.ServerWorkingDirectory}.");
        // Real settings arrive with the options page; until then the defaults apply.
        return (workspace, new LanguageServerOptions());
    }

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
            _watcher = new FileSystemWatcher(root, "*.yaml") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            _watcher.Changed += (_, e) => _host.OnFileChanged(e.FullPath);
            _watcher.Created += (_, e) => _host.OnFileChanged(e.FullPath);
            _watcher.Deleted += (_, e) => _host.OnFileChanged(e.FullPath);
            _watcher.Renamed += (_, e) => _host.OnFileChanged(e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }
    }

    private async Task RaiseAsync(AsyncEventHandler<EventArgs>? handler)
    {
        if (handler is not null)
        {
            await handler.InvokeAsync(this, EventArgs.Empty).ConfigureAwait(false);
        }
    }

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
