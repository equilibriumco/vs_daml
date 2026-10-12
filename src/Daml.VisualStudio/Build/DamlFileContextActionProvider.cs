using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core;
using Daml.VisualStudio.Core.Build;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Extensions.VS;

namespace Daml.VisualStudio.Build;

/// <summary>
/// Runs Test from the folder-view menu. Build and Clean need no action: the folder view's own
/// commands run their contexts directly (<see cref="DamlBuildConfigurationContext"/>). Test has
/// no built-in command, so it is our menu command, which this action answers. Its output stays in
/// the build pane: the compile errors it would add are the ones Build already lists, and build
/// messages can't be withdrawn, so reporting them again would only duplicate entries.
/// </summary>
[ExportFileContextActionProvider(
    (FileContextActionProviderOptions)VsCommandActionProviderOptions.SupportVsCommands,
    BuildIds.ActionProviderString,
    ProviderPriority.Normal,
    BuildIds.TestContextTypeString)]
internal sealed class DamlFileContextActionProviderFactory : IWorkspaceProviderFactory<IFileContextActionProvider>, IVsCommandActionProvider
{
    private static readonly IReadOnlyList<CommandID> Commands = [new CommandID(BuildIds.CommandSet, (int)BuildIds.TestCommandId)];

    private readonly Func<DamlBuildAction, CancellationToken, Task<DamlBuildRun>> _run;

    [ImportingConstructor]
    public DamlFileContextActionProviderFactory(DamlBuildService service)
        : this(service.RunAsync)
    {
    }

    internal DamlFileContextActionProviderFactory(Func<DamlBuildAction, CancellationToken, Task<DamlBuildRun>> run) => _run = run;

    public IFileContextActionProvider CreateProvider(IWorkspace workspaceContext) =>
        new Provider(_run);

    public IReadOnlyCollection<CommandID> GetSupportedVsCommands() => Commands;

    private sealed class Provider : IFileContextActionProvider
    {
        private readonly Func<DamlBuildAction, CancellationToken, Task<DamlBuildRun>> _run;

        public Provider(Func<DamlBuildAction, CancellationToken, Task<DamlBuildRun>> run) => _run = run;

        public Task<IReadOnlyList<IFileContextAction>> GetActionsAsync(string filePath, FileContext fileContext, CancellationToken cancellationToken)
        {
            IReadOnlyList<IFileContextAction> actions = fileContext.Context is DamlBuildConfigurationContext { Action.Kind: DpmCommandKind.Test } context
                ? [new TestAction(_run, context, fileContext)]
                : [];
            return Task.FromResult(actions);
        }
    }

    private sealed class TestAction : IFileContextAction, IVsCommandItem
    {
        private readonly Func<DamlBuildAction, CancellationToken, Task<DamlBuildRun>> _run;
        private readonly DamlBuildConfigurationContext _context;

        public TestAction(
            Func<DamlBuildAction, CancellationToken, Task<DamlBuildRun>> run,
            DamlBuildConfigurationContext context,
            FileContext source)
        {
            _run = run;
            _context = context;
            Source = source;
        }

        public string DisplayName => _context.Action.DisplayName;

        public FileContext Source { get; }

        public Guid CommandGroup => BuildIds.CommandSet;

        public uint CommandId => BuildIds.TestCommandId;

        public async Task<IFileContextActionResult> ExecuteAsync(IProgress<IFileContextActionProgressUpdate> progress, CancellationToken cancellationToken)
        {
            var run = await _run(_context.Action, cancellationToken).ConfigureAwait(false);
            return new FileContextActionResult(run.Succeeded);
        }
    }
}
