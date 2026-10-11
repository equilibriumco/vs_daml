using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class OpenDamlFileTests
{
    private const string PackageGuid = "117354e2-f560-49ff-8753-121bb9a6b227";

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Opening_a_daml_file_uses_the_daml_content_type_and_loads_the_package()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var file = Path.Combine(RepoRoot(), "samples", "broken", "daml", "TypeError.daml");

        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file, Guid.Empty, out _, out _, out _, out IVsTextView textView);

        var adapters = RequiredService<SComponentModel, IComponentModel>().GetService<IVsEditorAdaptersFactoryService>();
        var view = adapters.GetWpfTextView(textView);
        Assert.NotNull(view);
        Assert.Equal("daml", view!.TextBuffer.ContentType.TypeName);

        // The package loads in the background once the daml editor is active; give it a moment.
        var shell = RequiredService<SVsShell, IVsShell>();
        var packageGuid = new Guid(PackageGuid);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        IVsPackage? package = null;
        while (DateTime.UtcNow < deadline && shell.IsPackageLoaded(ref packageGuid, out package) != 0)
        {
            await Task.Delay(250);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        }
        Assert.NotNull(package);
    }

    private static TInterface RequiredService<TService, TInterface>()
        where TInterface : class =>
        ServiceProvider.GlobalProvider.GetService(typeof(TService)) as TInterface
            ?? throw new InvalidOperationException($"{typeof(TService).Name} is not available.");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(OpenDamlFileTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daml.VisualStudio.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found above the test assembly.");
    }
}
