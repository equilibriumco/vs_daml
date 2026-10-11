using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core;
using Daml.VisualStudio.Core.Build;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;
using Microsoft.VisualStudio.Workspace.Extensions.VS;

namespace Daml.VisualStudio.Build;

/// <summary>
/// Turns the contexts from <see cref="DamlFileContextProviderFactory"/> into runnable actions.
/// Build and Clean appear under the folder view's built-in commands; Test comes with its own
/// menu command.
/// </summary>
[ExportFileContextActionProvider(
    (FileContextActionProviderOptions)VsCommandActionProviderOptions.SupportVsCommands,
    BuildIds.ActionProviderString,
    ProviderPriority.Normal,
    BuildContextTypes.BuildContextType,
    BuildContextTypes.CleanContextType,
    BuildIds.TestContextTypeString)]
internal sealed class DamlFileContextActionProviderFactory : IWorkspaceProviderFactory<IFileContextActionProvider>, IVsCommandActionProvider
{
    private static readonly IReadOnlyList<CommandID> Commands = [new CommandID(BuildIds.CommandSet, BuildIds.TestCommandId)];

    private readonly DamlBuildService _service;

    [ImportingConstructor]
    public DamlFileContextActionProviderFactory(DamlBuildService service) => _service = service;

    public IFileContextActionProvider CreateProvider(IWorkspace workspaceContext) => new Provider(_service);

    public IReadOnlyCollection<CommandID> GetSupportedVsCommands() => Commands;

    private sealed class Provider : IFileContextActionProvider
    {
        private readonly DamlBuildService _service;

        public Provider(DamlBuildService service) => _service = service;

        public Task<IReadOnlyList<IFileContextAction>> GetActionsAsync(string filePath, FileContext fileContext, CancellationToken cancellationToken)
        {
            // Other providers' build contexts (CMake, MSBuild, ...) come here too; only ours carry a DamlBuildAction.
            IReadOnlyList<IFileContextAction> actions = fileContext.Context is DamlBuildAction action
                ? [action.Kind == DpmCommandKind.Test ? new TestAction(_service, action, fileContext) : new Action(_service, action, fileContext)]
                : [];
            return Task.FromResult(actions);
        }
    }

    private class Action : IFileContextAction
    {
        private readonly DamlBuildService _service;
        private readonly DamlBuildAction _action;

        public Action(DamlBuildService service, DamlBuildAction action, FileContext source)
        {
            _service = service;
            _action = action;
            Source = source;
        }

        public string DisplayName => _action.DisplayName;

        public FileContext Source { get; }

        public async Task<IFileContextActionResult> ExecuteAsync(IProgress<IFileContextActionProgressUpdate> progress, CancellationToken cancellationToken) =>
            new FileContextActionResult(await _service.RunAsync(_action, cancellationToken).ConfigureAwait(false));
    }

    private sealed class TestAction : Action, IVsCommandItem
    {
        public TestAction(DamlBuildService service, DamlBuildAction action, FileContext source)
            : base(service, action, source)
        {
        }

        public Guid CommandGroup => BuildIds.CommandSet;

        public uint CommandId => BuildIds.TestCommandId;
    }
}
