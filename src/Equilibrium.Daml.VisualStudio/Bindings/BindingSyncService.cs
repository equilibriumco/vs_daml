using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageClient;
using Equilibrium.Daml.VisualStudio.Options;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TaskStatusCenter;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.Bindings;

/// <summary>
/// Background sync of bindings, when the user turned it on: a saved Daml file or manifest makes the
/// C# projects generating from its package (or from packages built on it) stale, and they are
/// refreshed a moment later, one at a time. Visual Studio builds pause refreshes, which resume to verify pending projects. Progress shows in
/// the Task Status Center, the outcome on the status bar and in the EQ Daml Tools: Build pane, errors in the
/// Error List. The policy lives in <see cref="BindingSyncScheduler"/> and <see cref="BindingSyncPlanner"/>;
/// this only connects them to Visual Studio.
/// </summary>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class BindingSyncService : IDisposable
{
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;
    private readonly DamlLanguageClient _client;
    private readonly DamlBindingService _bindings;
    private readonly SolutionProjectConfigurations _solution;
    private readonly BindingErrorList _errors;
    private readonly BindingSyncScheduler _scheduler;
    private readonly BindingSyncPlanner _planner;
    private readonly BindingConsumerReader _consumers = new(new GenerationCatalog());
    private readonly Dictionary<BindingRefreshTarget, TaskCompletionSource<bool>> _progress = [];
    private readonly object _gate = new();
    private bool _started;

    [ImportingConstructor]
    public BindingSyncService(JoinableTaskContext joinableTaskContext, IDamlLogger logger, DamlLanguageClient client, DamlBindingService bindings)
    {
        _joinableTaskFactory = joinableTaskContext.Factory;
        _logger = logger;
        _client = client;
        _bindings = bindings;
        _solution = new SolutionProjectConfigurations(_joinableTaskFactory);
        _errors = new BindingErrorList(_joinableTaskFactory);
        var files = new PhysicalFileSystem();
        _planner = new BindingSyncPlanner(files, new YamlManifestReader(files));
        _scheduler = new BindingSyncScheduler(new MSBuildBindingRefresher(logger, new ProcessRunner(), _errors, _solution, OnFilesRemoved), TimeProvider.System, logger);
        _scheduler.StatusChanged += OnStatusChanged;
    }

    /// <summary>Starts listening to saves, builds and solution changes; again, nothing more happens.</summary>
    public async Task StartAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        if (_started)
        {
            return;
        }
        _started = true;
        VS.Events.DocumentEvents.Saved += OnSaved;
        VS.Events.BuildEvents.SolutionBuildStarted += OnBuildStarted;
        VS.Events.BuildEvents.SolutionBuildDone += OnBuildDone;
        VS.Events.BuildEvents.SolutionBuildCancelled += OnBuildCancelled;
        VS.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionChanged;
        VS.Events.SolutionEvents.OnAfterCloseSolution += OnSolutionClosed;
        VS.Events.SolutionEvents.OnAfterOpenProject += OnSolutionChanged;
        VS.Events.SolutionEvents.OnBeforeCloseProject += OnSolutionChanged;
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _errors.Dispose();
    }

    private void OnSaved(string? path)
    {
        if (path is not null && (path.EndsWith(".daml", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("daml.yaml", StringComparison.OrdinalIgnoreCase)))
        {
            _joinableTaskFactory.RunAsync(() => OnDamlSavedAsync(path)).FileAndForget("daml/bindingSync/saved");
        }
    }

    private async Task OnDamlSavedAsync(string path)
    {
        if (!(await DamlOptions.GetSettingsAsync()).LiveBindingSync)
        {
            return;
        }
        var projects = await _solution.WithBindingsAsync();
        await TaskScheduler.Default;
        var consumers = projects.SelectMany(p => _consumers.Read(p.Project, BindingRefreshCommand.Configuration(p.Configuration).Configuration, p.IntermediatePath)).ToList();
        var packages = (_client.Host.Workspace?.Packages ?? []).Select(p => Path.Combine(p, "daml.yaml"));
        var targets = _planner.TargetsFor(path, consumers, packages);
        _logger.Log(DamlLogCategory.Build, DamlLogLevel.Debug, targets.Count == 0
            ? $"{Path.GetFileName(path)} saved; no C# project generates from its package yet."
            : $"{Path.GetFileName(path)} saved; the Daml bindings of {string.Join(", ", targets.Select(Name))} are refreshed in a moment.");
        _scheduler.Request(targets);
    }

    private void OnBuildStarted(object? sender, EventArgs e) => _scheduler.BuildStarted();

    private void OnBuildDone(bool succeeded) => _scheduler.BuildFinished(succeeded);

    private void OnBuildCancelled() => _scheduler.BuildFinished(succeeded: false);

    private void OnSolutionChanged(object? item) => _scheduler.Clear();

    private void OnSolutionClosed() => _scheduler.Clear();

    private void OnStatusChanged(object? sender, BindingSyncStatus status)
    {
        var name = Name(status.Target);
        switch (status.State)
        {
            case BindingSyncState.Updating:
                ShowProgress(status.Target, name);
                _joinableTaskFactory.RunAsync(() => VS.StatusBar.ShowMessageAsync($"EQ Daml: updating the C# bindings of {name}…")).FileAndForget("daml/bindingSync/status");
                break;
            case BindingSyncState.Ready:
                EndProgress(status.Target);
                // Published outside Visual Studio, like a build, so the index reads it whole.
                _bindings.ReadAgain();
                _logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, $"The Daml bindings of {name} are up to date{(status.Message is { } how ? ": " + how : ".")}");
                _joinableTaskFactory.RunAsync(() => VS.StatusBar.ShowMessageAsync($"EQ Daml: the C# bindings of {name} are up to date")).FileAndForget("daml/bindingSync/status");
                break;
            case BindingSyncState.Failed:
                EndProgress(status.Target);
                _logger.Log(DamlLogCategory.Build, DamlLogLevel.Warning, $"The Daml bindings of {name} couldn't be updated and stay as they were: {status.Message}");
                _joinableTaskFactory.RunAsync(() => VS.StatusBar.ShowMessageAsync($"EQ Daml: the C# bindings of {name} are out of date ({status.Message})")).FileAndForget("daml/bindingSync/status");
                break;
            case BindingSyncState.Cancelled:
                EndProgress(status.Target);
                _logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, $"The update of the Daml bindings of {name} was cancelled; they stay as they were until the next Daml change or build.");
                _joinableTaskFactory.RunAsync(() => VS.StatusBar.ShowMessageAsync($"EQ Daml: the update of the C# bindings of {name} was cancelled")).FileAndForget("daml/bindingSync/status");
                break;
            default:
                // Stale again while refreshing: the running entry ends, a new one starts with the next run.
                EndProgress(status.Target);
                break;
        }
    }

    // One Task Status Center entry per running refresh; cancelling it stops background sync's work for now.
    private void ShowProgress(BindingRefreshTarget target, string name)
    {
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _progress[target] = finished;
        }
        _joinableTaskFactory.RunAsync(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            var statusCenter = await VS.Services.GetTaskStatusCenterAsync();
            var options = default(TaskHandlerOptions);
            options.Title = "Daml bindings";
            options.ActionsAfterCompletion = CompletionActions.None;
            var data = default(TaskProgressData);
            data.CanBeCanceled = true;
            data.ProgressText = $"Updating the C# bindings of {name}";
            var handler = statusCenter.PreRegister(options, data);
            handler.RegisterTask(finished.Task);
            handler.UserCancellation.Register(_scheduler.Clear);
        }).FileAndForget("daml/bindingSync/progress");
    }

    private void EndProgress(BindingRefreshTarget target)
    {
        TaskCompletionSource<bool>? finished;
        lock (_gate)
        {
            if (_progress.TryGetValue(target, out finished))
            {
                _progress.Remove(target);
            }
        }
        finished?.TrySetResult(true);
    }

    // New generated types reach IntelliSense when the project is evaluated again, removed ones only
    // with a build or a reload; the bar says so and offers the reload.
    private void OnFilesRemoved(BindingRefreshTarget target, int count) =>
        _joinableTaskFactory.RunAsync(async () =>
        {
            const string Reload = "Reload project";
            var name = Name(target);
            var text = $"The last Daml change removed {count} generated file{(count == 1 ? "" : "s")} of {name}; IntelliSense may keep offering their types until the project is built or reloaded. ";
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(text) }, new[] { new InfoBarHyperlink(Reload) }, KnownMonikers.StatusInformation, isCloseButtonVisible: true);
            var infoBar = await VS.InfoBar.CreateAsync(model);
            if (infoBar is null)
            {
                _logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, text);
                return;
            }
            infoBar.ActionItemClicked += (_, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread(); // VS raises it from the info bar's UI
                args.InfoBarUIElement.Close();
                _joinableTaskFactory.RunAsync(() => _solution.ReloadAsync(target.Project)).FileAndForget("daml/bindingSync/reload");
            };
            await infoBar.TryShowInfoBarUIAsync();
        }).FileAndForget("daml/bindingSync/filesRemoved");

    private static string Name(BindingRefreshTarget target) =>
        Path.GetFileNameWithoutExtension(target.Project) + (target.TargetFramework is { } framework ? $" ({framework})" : "");
}
