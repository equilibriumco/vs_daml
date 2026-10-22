using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.Bindings;

/// <summary>
/// The errors and warnings of background refreshes in the Error List, per C# project: each refresh
/// replaces what the one before it reported, so a successful one clears them.
/// </summary>
internal sealed class BindingErrorList : IDisposable
{
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Dictionary<string, List<ErrorTask>> _byProject = new(StringComparer.OrdinalIgnoreCase);
    private ErrorListProvider? _provider;

    public BindingErrorList(JoinableTaskFactory joinableTaskFactory) => _joinableTaskFactory = joinableTaskFactory;

    /// <summary>Shows <paramref name="diagnostics"/> as what refreshing <paramref name="project"/> reported.</summary>
    public async Task ShowAsync(string project, IReadOnlyList<MSBuildDiagnostic> diagnostics)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        var provider = _provider ??= new ErrorListProvider(ServiceProvider.GlobalProvider) { ProviderName = "Daml bindings" };
        if (_byProject.TryGetValue(project, out var previous))
        {
            foreach (var task in previous)
            {
                provider.Tasks.Remove(task);
            }
        }
        var tasks = diagnostics.Select(d => Task(provider, d)).ToList();
        foreach (var task in tasks)
        {
            provider.Tasks.Add(task);
        }
        _byProject[project] = tasks;
    }

    public void Dispose()
    {
        // ErrorListProvider is a UI-thread object; disposing it elsewhere would throw.
        if (ThreadHelper.CheckAccess())
        {
            _provider?.Dispose();
        }
    }

    private static ErrorTask Task(ErrorListProvider provider, MSBuildDiagnostic diagnostic)
    {
        var task = new ErrorTask
        {
            Document = diagnostic.File,
            Line = Math.Max(0, diagnostic.Line - 1),
            Column = Math.Max(0, diagnostic.Column - 1),
            Text = $"{diagnostic.Code}: {diagnostic.Message}",
            ErrorCategory = diagnostic.IsError ? TaskErrorCategory.Error : TaskErrorCategory.Warning,
            Category = TaskCategory.BuildCompile,
        };
        task.Navigate += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // the Error List raises it
            provider.Navigate(task, VSConstants.LOGVIEWID_Code);
        };
        return task;
    }
}
