using Equilibrium.Daml.Build.Core;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Build;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Build;

public class CurrentPackageActionTests
{
    private readonly InMemoryFileSystem _files = new();

    private WorkspaceLocator Locator() => new(_files, new YamlManifestReader(_files));

    [Theory, Trait("Category", "Unit")]
    [InlineData(DpmCommandKind.Build)]
    [InlineData(DpmCommandKind.Test)]
    public void The_active_file_runs_in_its_own_package(DpmCommandKind kind)
    {
        _files.AddFile(@"C:\repo\multi-package.yaml", "packages:\n  - app\n");
        _files.AddFile(@"C:\repo\app\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");

        var action = DamlBuildActions.ForPackageOf(Locator(), @"C:\repo\app\daml\Sub\Main.daml", kind).ShouldNotBeNull();

        action.ShouldBe(new DamlBuildAction(kind, @"C:\repo\app", AllPackages: false));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\elsewhere\Loose.daml")]
    [InlineData(null)]
    [InlineData("")]
    public void A_file_outside_any_package_has_nothing_to_run(string? file)
    {
        _files.AddFile(@"C:\repo\app\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");

        DamlBuildActions.ForPackageOf(Locator(), file, DpmCommandKind.Build).ShouldBeNull();
    }
}
