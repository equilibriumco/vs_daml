using Daml.Build.Core.Manifests;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.LanguageServer.Hosting;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Hosting;

public class WorkspaceDiscoveryTests
{
    private readonly InMemoryFileSystem _files = new();

    private WorkspaceContext? Discover(string? root, string? activeFile) =>
        new WorkspaceDiscovery(_files, new YamlManifestReader(_files)).Discover(root, activeFile, version: 3);

    [Fact, Trait("Category", "Unit")]
    public void The_active_file_decides_the_package_and_server_folder()
    {
        _files.AddFile(@"C:\repo\multi-package.yaml", "packages:\n  - app\n");
        _files.AddFile(@"C:\repo\app\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");

        var context = Discover(@"C:\repo", @"C:\repo\app\daml\Main.daml").ShouldNotBeNull();

        context.Mode.ShouldBe(ServerMode.MultiIde);
        context.PackageRoot.ShouldBe(@"C:\repo\app");
        context.ServerWorkingDirectory.ShouldBe(@"C:\repo");
        context.Version.ShouldBe(3);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_single_package_server_runs_in_the_active_files_package()
    {
        _files.AddFile(@"C:\repo\multi-package.yaml", "packages:\n  - app\n");
        _files.AddFile(@"C:\repo\app\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");
        var discovery = new WorkspaceDiscovery(_files, new YamlManifestReader(_files));

        var context = discovery.Discover(@"C:\repo", @"C:\repo\app\daml\Main.daml", version: 1, ServerMode.Ide).ShouldNotBeNull();

        context.Mode.ShouldBe(ServerMode.Ide);
        context.ServerWorkingDirectory.ShouldBe(@"C:\repo\app");
        discovery.Discover(@"C:\repo", null, version: 2, ServerMode.Ide).ShouldBeNull("no package to serve without a Daml file");
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_an_active_file_a_manifest_at_the_root_is_enough()
    {
        _files.AddFile(@"C:\repo\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");

        Discover(@"C:\repo", null).ShouldNotBeNull().PackageRoot.ShouldBe(@"C:\repo");
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_a_folder_the_active_files_package_is_the_workspace()
    {
        _files.AddFile(@"C:\repo\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");

        var context = Discover(null, @"C:\repo\daml\Main.daml").ShouldNotBeNull();

        context.WorkspaceRoot.ShouldBe(@"C:\repo");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\loose", @"C:\loose\Scratch.daml")]
    [InlineData(@"C:\loose", null)]
    [InlineData(null, null)]
    public void No_manifest_anywhere_means_no_workspace(string? root, string? activeFile) =>
        Discover(root, activeFile).ShouldBeNull();

    // The real sample, with the importing package's file active, the way Visual Studio asks.
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"main\daml\Main.daml")]
    [InlineData(@"interfaces\daml\Asset.daml")]
    [InlineData(null)]
    public void A_file_of_the_multi_package_sample_is_served_from_its_root(string? activeFile)
    {
        var files = new Daml.Build.Core.FileSystem.PhysicalFileSystem();
        var root = Repository.Sample("multi-package");

        var context = new WorkspaceDiscovery(files, new YamlManifestReader(files))
            .Discover(root, activeFile is null ? null : System.IO.Path.Combine(root, activeFile), version: 1);

        context.ShouldNotBeNull();
        context.ServerWorkingDirectory.ShouldBe(root);
    }
}
