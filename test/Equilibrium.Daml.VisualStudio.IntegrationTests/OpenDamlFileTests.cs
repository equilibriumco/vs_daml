using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

public class OpenDamlFileTests
{
    private const string PackageGuid = "117354e2-f560-49ff-8753-121bb9a6b227";

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Opening_a_daml_file_uses_the_daml_content_type_and_loads_the_package()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var view = Ide.OpenFile(Path.Combine(Ide.CopySample("broken"), "daml", "TypeError.daml"));
        try
        {
            Assert.Equal("daml", view.TextBuffer.ContentType.TypeName);

            // The package loads in the background once the daml editor is active; give it a moment.
            var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) as IVsShell ?? throw new InvalidOperationException("SVsShell is not available.");
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
        finally
        {
            Ide.CloseAllDocuments();
        }
    }
}
