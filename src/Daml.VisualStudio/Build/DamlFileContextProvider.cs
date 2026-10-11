using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core;
using Daml.VisualStudio.Core.Build;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;

namespace Daml.VisualStudio.Build;

/// <summary>
/// Tells Open Folder that daml.yaml and multi-package.yaml can be built, tested and cleaned.
/// Which commands a manifest offers is decided by <see cref="DamlBuildActions"/>.
/// </summary>
[ExportFileContextProvider(BuildIds.FileContextProviderString, BuildContextTypes.BuildContextType, BuildContextTypes.CleanContextType, BuildIds.TestContextTypeString)]
internal sealed class DamlFileContextProviderFactory : IWorkspaceProviderFactory<IFileContextProvider>
{
    private readonly Func<DamlBuildAction, CancellationToken, Task<bool>> _run;

    [ImportingConstructor]
    public DamlFileContextProviderFactory(DamlBuildService service)
        : this(service.RunAsync)
    {
    }

    internal DamlFileContextProviderFactory(Func<DamlBuildAction, CancellationToken, Task<bool>> run) => _run = run;

    public IFileContextProvider CreateProvider(IWorkspace workspaceContext) => new Provider(_run);

    internal static Guid ContextTypeFor(DpmCommandKind kind) => kind switch
    {
        DpmCommandKind.Build => BuildContextTypes.BuildContextTypeGuid,
        DpmCommandKind.Clean => BuildContextTypes.CleanContextTypeGuid,
        _ => BuildIds.TestContextType,
    };

    private sealed class Provider : IFileContextProvider
    {
        private readonly Func<DamlBuildAction, CancellationToken, Task<bool>> _run;

        public Provider(Func<DamlBuildAction, CancellationToken, Task<bool>> run) => _run = run;

        public Task<IReadOnlyCollection<FileContext>> GetContextsForFileAsync(string filePath, CancellationToken cancellationToken)
        {
            IReadOnlyCollection<FileContext> contexts = DamlBuildActions.For(filePath)
                .Select(action => new FileContext(
                    BuildIds.FileContextProvider,
                    ContextTypeFor(action.Kind),
                    new DamlBuildConfigurationContext(action, _run),
                    [filePath],
                    action.DisplayName))
                .ToList();
            return Task.FromResult(contexts);
        }
    }
}
