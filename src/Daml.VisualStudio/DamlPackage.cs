using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.Logging;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

// VS and its bundled extensions carry YamlDotNet 12 and 13; an exact codebase makes our version
// resolve to our copy instead of whichever one was probed or loaded first.
[assembly: ProvideCodeBase(AssemblyName = "YamlDotNet", Version = "18.0.0.0", PublicKeyToken = "ec19458f3c15af5e", CodeBase = "$PackageFolder$\\YamlDotNet.dll")]

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
