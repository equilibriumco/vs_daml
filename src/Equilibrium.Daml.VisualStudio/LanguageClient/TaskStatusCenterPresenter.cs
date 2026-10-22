using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Messages;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TaskStatusCenter;
using Microsoft.VisualStudio.Threading;

namespace Equilibrium.Daml.VisualStudio.LanguageClient;

/// <summary>Shows SDK installations in the Task Status Center, where the user can cancel them.</summary>
internal sealed class TaskStatusCenterPresenter : ISdkInstallPresenter
{
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;

    public TaskStatusCenterPresenter(JoinableTaskFactory joinableTaskFactory, IDamlLogger logger)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _logger = logger;
    }

    public ISdkInstallView Show(string title, Func<Task> cancel)
    {
        var view = new View();
        _joinableTaskFactory.RunAsync(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            var statusCenter = await VS.Services.GetTaskStatusCenterAsync();
            var options = default(TaskHandlerOptions);
            options.Title = title;
            options.ActionsAfterCompletion = CompletionActions.None;
            var data = default(TaskProgressData);
            data.CanBeCanceled = true;
            var handler = statusCenter.PreRegister(options, data);
            handler.RegisterTask(view.Finished);
            handler.UserCancellation.Register(() =>
                _joinableTaskFactory.RunAsync(cancel).FileAndForget("daml/sdkInstall/cancel"));
            view.Attach(handler);
        }).FileAndForget("daml/sdkInstall/show");
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"Task Status Center: {title}");
        return view;
    }

    // Updates can arrive before the Task Status Center entry exists; the latest is applied then.
    private sealed class View : ISdkInstallView
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITaskHandler? _handler;
        private int? _pending;

        public Task Finished => _finished.Task;

        public void Attach(ITaskHandler handler)
        {
            int? pending;
            lock (_gate)
            {
                _handler = handler;
                pending = _pending;
            }
            if (pending is { } percent)
            {
                Apply(handler, percent);
            }
        }

        public void Report(int percentComplete)
        {
            ITaskHandler? handler;
            lock (_gate)
            {
                handler = _handler;
                _pending = percentComplete;
            }
            if (handler is not null)
            {
                Apply(handler, percentComplete);
            }
        }

        public void Complete() => _finished.TrySetResult(true);

        private static void Apply(ITaskHandler handler, int percent)
        {
            var data = default(TaskProgressData);
            data.CanBeCanceled = true;
            data.PercentComplete = percent;
            handler.Progress.Report(data);
        }
    }
}
