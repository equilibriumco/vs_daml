using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Project;

// The checked-in samples on disk, read through the real file system: the layouts damlc was tried against.
public class SampleWorkspaceTests
{
    private readonly WorkspaceLocator _locator;

    public SampleWorkspaceTests()
    {
        var files = new PhysicalFileSystem();
        _locator = new WorkspaceLocator(files, new YamlManifestReader(files));
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_sample_manifest_reads_without_problems()
    {
        foreach (var file in new[] { @"single-package\daml\Main.daml", @"multi-package\main\daml\Main.daml", @"mixed-dotnet\daml\test\daml\MiniDemo\AssetTest.daml", @"broken\daml\TypeError.daml" })
        {
            var location = _locator.FindForFile(Repository.Sample(file));
            location.Diagnostics.ShouldBeEmpty(file);
            location.Package.ShouldNotBeNull(file).SdkVersion.ShouldBe("3.6.0", file);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void Open_folder_on_samples_runs_the_server_in_the_multi_package_folder_for_its_files()
    {
        var context = _locator.Resolve(Repository.Sample(""), Repository.Sample(@"multi-package\main\daml\Main.daml"), ServerMode.MultiIde, 1).Context.ShouldNotBeNull();

        context.ServerWorkingDirectory.ShouldBe(Repository.Sample("multi-package"));
        context.Packages.Count.ShouldBe(3);
    }

    [Fact, Trait("Category", "Unit")]
    public void Main_imports_Asset_from_the_interfaces_package_it_depends_on()
    {
        var files = new PhysicalFileSystem();
        var reader = new YamlManifestReader(files);
        var main = reader.ReadPackage(Repository.Sample(@"multi-package\main\daml.yaml")).Value.ShouldNotBeNull();
        var interfaces = reader.ReadPackage(Repository.Sample(@"multi-package\interfaces\daml.yaml")).Value.ShouldNotBeNull();

        var result = new ModulePathResolver(files).Resolve("Asset", main, [main, interfaces]);

        result.Status.ShouldBe(ModuleResolutionStatus.Found);
        result.Path.ShouldBe(Repository.Sample(@"multi-package\interfaces\daml\Asset.daml"));
    }

    [Fact, Trait("Category", "Unit")]
    public void The_nested_test_package_of_mixed_dotnet_is_listed_by_its_multi_package()
    {
        var location = _locator.FindForFile(Repository.Sample(@"mixed-dotnet\daml\test\daml\MiniDemo\AssetTest.daml"));

        location.Package.ShouldNotBeNull().Name.ShouldBe("mixed-dotnet-contracts-test");
        location.MultiPackage.ShouldNotBeNull().ManifestPath.ShouldBe(Repository.Sample(@"mixed-dotnet\multi-package.yaml"));
    }
}
