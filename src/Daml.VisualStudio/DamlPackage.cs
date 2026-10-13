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

// Same for Microsoft.Bcl.TimeProvider: VS itself doesn't ship it, only other extensions'
// private folders do.
[assembly: ProvideCodeBase(AssemblyName = "Microsoft.Bcl.TimeProvider", Version = "8.0.0.1", PublicKeyToken = "cc7b13ffcd2ddd51", CodeBase = "$PackageFolder$\\Microsoft.Bcl.TimeProvider.dll")]

namespace Daml.VisualStudio;

/// <summary>
/// Entry point of the extension. Loads in the background, and only once Daml content shows up:
/// a .daml file in the active editor, or a Daml file selected in Solution Explorer.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuids.PackageString)]
[ProvideBindingPath]
[ProvideMenuResource("Menus.ctmenu", 1)]
// Script results open beside the Output window, one tab per script. They aren't restored with
// the layout: their content comes from a running language server and is shown again on request.
[ProvideToolWindow(typeof(ScriptResults.ScriptResultsToolWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, Window = OutputWindowGuid)]
[ProvideOptionPage(typeof(Options.DamlOptionPages.General), "Daml", "General", 0, 0, true, SupportsProfiles = true)]
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
    // The Output window, which the results windows dock beside.
    private const string OutputWindowGuid = "34e76e81-ee4a-11d0-ae2e-00a0c90fffc3";

    /// <inheritdoc />
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // Stays on the background thread: nothing here needs the UI thread.
        var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var logger = componentModel?.GetService<IDamlLogger>() ?? NullDamlLogger.Instance;

        var version = typeof(DamlPackage).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Daml extension loaded (version {version})");

        // The Extensions > Daml menu.
        await this.RegisterCommandsAsync();
    }
}
