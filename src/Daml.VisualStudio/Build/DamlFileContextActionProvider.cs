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
/// Build and Clean answer the folder view's own Build and Clean commands; Test is listed in the
/// folder-view menu by Open Folder itself, under its display name.
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
    private static readonly IReadOnlyList<CommandID> Commands =
    [
        new CommandID(BuildIds.VsBuildCommandGroup, (int)BuildIds.BuildCommandId),
        new CommandID(BuildIds.VsBuildCommandGroup, (int)BuildIds.CleanCommandId),
    ];

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
            // Other providers' build contexts (CMake, MSBuild, ...) come here too; only ours carry a Daml action.
            IReadOnlyList<IFileContextAction> actions = fileContext.Context is DamlBuildConfigurationContext { Action: var action }
                ? [action.Kind switch
                {
                    DpmCommandKind.Build => new CommandAction(_service, action, fileContext, BuildIds.BuildCommandId),
                    DpmCommandKind.Clean => new CommandAction(_service, action, fileContext, BuildIds.CleanCommandId),
                    _ => new Action(_service, action, fileContext),
                }]
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

    // An action the folder view's own Build or Clean command runs.
    private sealed class CommandAction : Action, IVsCommandItem
    {
        public CommandAction(DamlBuildService service, DamlBuildAction action, FileContext source, uint commandId)
            : base(service, action, source) => CommandId = commandId;

        public Guid CommandGroup => BuildIds.VsBuildCommandGroup;

        public uint CommandId { get; }
    }
}
