using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.Logging;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Daml.VisualStudio;

/// <summary>
/// Entry point of the extension. Loads in the background, and only once Daml content shows up:
/// a .daml file in the active editor, or a Daml file selected in Solution Explorer.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuids.PackageString)]
[ProvideBindingPath]
[ProvideAutoLoad(PackageGuids.DamlActiveString, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideUIContextRule(
    PackageGuids.DamlActiveString,
    name: "Daml content active",
    expression: "DamlEditor | DamlSelection",
    termNames: new[] { "DamlEditor", "DamlSelection" },
    termValues: new[]
    {
        "ActiveEditorContentType:" + DamlContentDefinition.ContentTypeName,
        @"HierSingleSelectionName:(\.daml|\.msbuildproj|(^|\\)daml\.yaml|(^|\\)multi-package\.yaml)$",
    })]
public sealed class DamlPackage : ToolkitPackage
{
    /// <inheritdoc />
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // Stays on the background thread: nothing here needs the UI thread.
        var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var logger = componentModel?.GetService<IDamlLogger>() ?? NullDamlLogger.Instance;

        var version = typeof(DamlPackage).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Daml extension loaded (version {version})");
    }
}
