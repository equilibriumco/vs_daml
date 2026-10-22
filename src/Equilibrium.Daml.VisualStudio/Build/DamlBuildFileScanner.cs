using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Build;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;
using Microsoft.VisualStudio.Workspace.Indexing;

namespace Equilibrium.Daml.VisualStudio.Build;

/// <summary>
/// Indexes Daml manifests as buildable. Open Folder keeps its Build and Clean commands disabled
/// for a file until the index holds a build configuration for it, so each manifest gets one,
/// "not applicable" (<c>null</c>), the same value its file contexts carry. On the first opening
/// of a folder the commands stay greyed out until indexing reaches the manifest.
/// </summary>
[ExportFileScanner(
    BuildIds.FileScannerString,
    "Daml",
    new[] { WorkspaceLocator.PackageManifestName, WorkspaceLocator.MultiPackageManifestName },
    new[] { typeof(IReadOnlyCollection<FileDataValue>) },
    ProviderPriority.Normal)]
internal sealed class DamlBuildFileScannerFactory : IWorkspaceProviderFactory<IFileScanner>
{
    public IFileScanner CreateProvider(IWorkspace workspaceContext) => new Scanner();

    private sealed class Scanner : IFileScanner
    {
        public Task<T> ScanContentAsync<T>(string filePath, CancellationToken cancellationToken)
            where T : class
        {
            if (typeof(T) != FileScannerTypeConstants.FileDataValuesType)
            {
                throw new NotImplementedException($"The Daml scanner only provides {FileScannerTypeConstants.FileDataValuesType.Name}.");
            }

            // The scanner is matched by file name suffix, so "not-daml.yaml" arrives here too.
            IReadOnlyCollection<FileDataValue> values = DamlBuildActions.For(filePath).Count > 0
                ? [new FileDataValue(BuildConfigurationContext.ContextTypeGuid, BuildConfigurationContext.DataValueName, value: null, target: null, context: DamlBuildConfigurationContext.ConfigurationName)]
                : [];
            return Task.FromResult((T)(object)values);
        }
    }
}
