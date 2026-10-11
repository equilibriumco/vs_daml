using Daml.TestHelpers;
using Daml.VisualStudio.Core.Project;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Project;

public class WorkspaceLocatorTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly WorkspaceLocator _locator;

    public WorkspaceLocatorTests() => _locator = new WorkspaceLocator(_files, new YamlManifestReader(_files));

    private void Package(string root, string name = "pkg") =>
        _files.AddFile(root + @"\daml.yaml", $"sdk-version: 3.6.0\nname: {name}\nsource: daml\nversion: 1.0.0\n")
              .AddFile(root + @"\daml\Main.daml", "module Main where\n");

    // The layout S-08 ran against: a multi-package tree and an independent package next to it.
    private void Samples()
    {
        _files.AddFile(@"C:\repo\samples\multi-package\multi-package.yaml", "packages:\n- ./interfaces\n- ./main\n");
        Package(@"C:\repo\samples\multi-package\interfaces", "interfaces");
        Package(@"C:\repo\samples\multi-package\main", "main");
        Package(@"C:\repo\samples\single-package", "single");
    }

    [Fact, Trait("Category", "Unit")]
    public void FindForFile_returns_the_nearest_package_and_the_multi_package_that_lists_it()
    {
        Samples();

        var location = _locator.FindForFile(@"C:/repo/samples/multi-package/main/daml/Main.daml");

        location.FilePath.ShouldBe(@"C:\repo\samples\multi-package\main\daml\Main.daml");
        location.Package.ShouldNotBeNull().PackageRoot.ShouldBe(@"C:\repo\samples\multi-package\main");
        location.Package.Name.ShouldBe("main");
        location.MultiPackage.ShouldNotBeNull().ManifestPath.ShouldBe(@"C:\repo\samples\multi-package\multi-package.yaml");
        location.Diagnostics.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void The_nearest_daml_yaml_wins_for_nested_packages()
    {
        Package(@"C:\repo\app\daml", "contracts");
        Package(@"C:\repo\app\daml\test", "contracts-test");

        _locator.FindForFile(@"C:\repo\app\daml\test\daml\Main.daml").Package.ShouldNotBeNull().Name.ShouldBe("contracts-test");
        _locator.FindForFile(@"C:\repo\app\daml\daml\Main.daml").Package.ShouldNotBeNull().Name.ShouldBe("contracts");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_multi_package_that_does_not_list_the_package_is_skipped_for_membership()
    {
        _files.AddFile(@"C:\repo\multi-package.yaml", "packages:\n- ./lib\n- ./app\n");
        _files.AddFile(@"C:\repo\app\multi-package.yaml", "packages:\n- ./other\n");
        Package(@"C:\repo\app");

        _locator.FindForFile(@"C:\repo\app\daml\Main.daml").MultiPackage.ShouldNotBeNull().ManifestPath.ShouldBe(@"C:\repo\multi-package.yaml");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_file_without_any_manifest_has_no_package()
    {
        _files.AddFile(@"C:\scratch\Lone.daml", "module Lone where\n");

        var location = _locator.FindForFile(@"C:\scratch\Lone.daml");

        location.Package.ShouldBeNull();
        location.MultiPackage.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Manifest_problems_travel_with_the_location()
    {
        _files.AddFile(@"C:\repo\app\daml.yaml", "name: [broken\n");

        var location = _locator.FindForFile(@"C:\repo\app\daml\Main.daml");

        location.Package.ShouldBeNull();
        location.Diagnostics.ShouldHaveSingleItem().Path.ShouldBe(@"C:\repo\app\daml.yaml");
    }

    [Fact, Trait("Category", "Unit")]
    public void Multi_ide_runs_in_the_nearest_folder_with_a_multi_package_yaml()
    {
        // Open Folder on the repo root: only a cwd at the multi-package folder lets navigation
        // between packages land in sources rather than package-database copies.
        Samples();

        var resolution = _locator.Resolve(@"C:\repo", @"C:\repo\samples\multi-package\main\daml\Main.daml", ServerMode.MultiIde, version: 1);

        var context = resolution.Context.ShouldNotBeNull();
        context.WorkspaceRoot.ShouldBe(@"C:\repo");
        context.ServerWorkingDirectory.ShouldBe(@"C:\repo\samples\multi-package");
        context.MultiPackageManifestPath.ShouldBe(@"C:\repo\samples\multi-package\multi-package.yaml");
        context.PackageRoot.ShouldBe(@"C:\repo\samples\multi-package\main");
        context.Packages.ShouldBe(new[] { @"C:\repo\samples\multi-package\interfaces", @"C:\repo\samples\multi-package\main" }, ignoreOrder: true);
        context.Mode.ShouldBe(ServerMode.MultiIde);
        context.Version.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_a_multi_package_yaml_multi_ide_runs_in_the_workspace_root()
    {
        Samples();

        var context = _locator.Resolve(@"C:\repo", @"C:\repo\samples\single-package\daml\Main.daml", ServerMode.MultiIde, 1).Context.ShouldNotBeNull();

        context.ServerWorkingDirectory.ShouldBe(@"C:\repo");
        context.MultiPackageManifestPath.ShouldBeNull();
        context.Packages.ShouldBe(new[] { @"C:\repo\samples\single-package" });
    }

    [Fact, Trait("Category", "Unit")]
    public void A_multi_package_yaml_above_the_workspace_root_keeps_the_cwd_at_the_root()
    {
        // damlc looks for multi-package.yaml upwards from its cwd, so the root already finds it.
        _files.AddFile(@"C:\repo\multi-package.yaml", "packages:\n- ./apps/app\n");
        Package(@"C:\repo\apps\app");

        var context = _locator.Resolve(@"C:\repo\apps", @"C:\repo\apps\app\daml\Main.daml", ServerMode.MultiIde, 1).Context.ShouldNotBeNull();

        context.ServerWorkingDirectory.ShouldBe(@"C:\repo\apps");
    }

    [Fact, Trait("Category", "Unit")]
    public void Ide_mode_runs_in_the_package_folder()
    {
        Samples();

        var context = _locator.Resolve(@"C:\repo", @"C:\repo\samples\single-package\daml\Main.daml", ServerMode.Ide, 1).Context.ShouldNotBeNull();

        context.ServerWorkingDirectory.ShouldBe(@"C:\repo\samples\single-package");
        context.Mode.ShouldBe(ServerMode.Ide);
    }

    [Fact, Trait("Category", "Unit")]
    public void Ide_mode_without_a_daml_yaml_cannot_start()
    {
        _files.AddFile(@"C:\repo\Lone.daml", "module Lone where\n");

        var resolution = _locator.Resolve(@"C:\repo", @"C:\repo\Lone.daml", ServerMode.Ide, 1);

        resolution.Context.ShouldBeNull();
        resolution.Problem.ShouldBe(WorkspaceProblem.NoPackageManifest);
    }

    [Fact, Trait("Category", "Unit")]
    public void Multi_ide_starts_for_a_file_without_a_daml_yaml()
    {
        // S-08: multi-ide still gives such a file diagnostics and hover.
        _files.AddFile(@"C:\repo\Lone.daml", "module Lone where\n");

        var context = _locator.Resolve(@"C:\repo", @"C:\repo\Lone.daml", ServerMode.MultiIde, 1).Context.ShouldNotBeNull();

        context.PackageRoot.ShouldBeNull();
        context.ServerWorkingDirectory.ShouldBe(@"C:\repo");
        context.Packages.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Files_under_the_root_or_a_listed_package_are_in_the_workspace()
    {
        _files.AddFile(@"C:\repo\app\multi-package.yaml", "packages:\n- ./main\n- ../../shared/lib\n");
        Package(@"C:\repo\app\main");
        Package(@"C:\shared\lib");

        var context = _locator.Resolve(@"C:\repo", @"C:\repo\app\main\daml\Main.daml", ServerMode.MultiIde, 1).Context.ShouldNotBeNull();

        context.Contains(@"C:\repo\app\main\daml\Main.daml").ShouldBeTrue();
        context.Contains(@"C:\shared\lib\daml\Main.daml").ShouldBeTrue();
        context.Contains(@"C:\elsewhere\Other.daml").ShouldBeFalse();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\app\main\daml.yaml", true)]
    [InlineData(@"C:\repo\new-package\daml.yaml", true)]
    [InlineData(@"C:\repo\app\multi-package.yaml", true)]
    [InlineData(@"C:\repo\MULTI-PACKAGE.YAML", true)]
    [InlineData(@"C:\shared\lib\daml.yaml", true)]
    [InlineData(@"C:\repo\app\main\daml\Main.daml", false)]
    [InlineData(@"C:\elsewhere\daml.yaml", false)]
    public void Manifest_changes_inside_the_workspace_invalidate_the_context(string changed, bool affected)
    {
        _files.AddFile(@"C:\repo\app\multi-package.yaml", "packages:\n- ./main\n- ../../shared/lib\n");
        Package(@"C:\repo\app\main");
        Package(@"C:\shared\lib");
        var context = _locator.Resolve(@"C:\repo", @"C:\repo\app\main\daml\Main.daml", ServerMode.MultiIde, 1).Context.ShouldNotBeNull();

        context.IsInvalidatedBy(changed).ShouldBe(affected);
    }

    [Fact, Trait("Category", "Unit")]
    public void Problems_in_the_manifests_used_are_reported_on_the_context()
    {
        _files.AddFile(@"C:\repo\multi-package.yaml", "packages: ./app\n");
        Package(@"C:\repo\app");

        var context = _locator.Resolve(@"C:\repo", @"C:\repo\app\daml\Main.daml", ServerMode.MultiIde, 1).Context.ShouldNotBeNull();

        context.Diagnostics.ShouldHaveSingleItem().Path.ShouldBe(@"C:\repo\multi-package.yaml");
        context.ServerWorkingDirectory.ShouldBe(@"C:\repo");
    }
}
