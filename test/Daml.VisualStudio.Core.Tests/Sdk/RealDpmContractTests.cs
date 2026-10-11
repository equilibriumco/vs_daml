using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.FileSystem;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Sdk;

// The installed dpm and SDK 3.6.0 (scripts/bootstrap.ps1 -Install): the outputs the unit tests
// replay must still be what the real tools print.
public class RealDpmContractTests
{
    private readonly ProcessRunner _runner = new();

    [SkippableFact, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    public async Task Dpm_lists_its_installed_sdks_in_the_shape_we_parse()
    {
        var dpm = new AssistantLocation(AssistantKind.Dpm, RequiredTools.Require(ExternalTool.Dpm));

        var active = await new DpmSdkResolver(_runner).ReadSdkVersionAsync(dpm, Repository.Root, CancellationToken.None);

        active.ShouldNotBeNullOrEmpty();
        SdkVersion.IsBefore(active!, 3, 4).ShouldBe(false);
    }

    [SkippableFact, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    public async Task Damlc_in_a_package_reports_its_version_and_server_modes()
    {
        var dpm = new AssistantLocation(AssistantKind.Dpm, RequiredTools.Require(ExternalTool.Dpm));

        var capabilities = await new DamlcProbe(_runner).ProbeAsync(dpm, Repository.Sample("single-package"), CancellationToken.None);

        capabilities.Version.ShouldNotBeNull().ShouldStartWith("3.6.");
        capabilities.SupportsIde.ShouldBeTrue();
        capabilities.SupportsMultiIde.ShouldBeTrue();
        capabilities.SupportsIdeIdentifier.ShouldBeTrue();
    }

    [SkippableFact, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    public async Task The_single_package_sample_resolves_to_dpm_and_its_pinned_sdk()
    {
        var dpmPath = RequiredTools.Require(ExternalTool.Dpm);
        var files = new PhysicalFileSystem();
        var workspace = new WorkspaceLocator(files, new YamlManifestReader(files))
            .Resolve(Repository.Sample("single-package"), Repository.Sample(@"single-package\daml\Main.daml"), ServerMode.Ide, 1)
            .Context.ShouldNotBeNull();
        var resolver = new SdkResolver(_runner, new AssistantLocator(), new YamlManifestReader(files));

        var sdk = await resolver.ResolveAsync(workspace, new LanguageServerOptions { DpmPath = dpmPath }, CancellationToken.None);

        sdk.Assistant.Kind.ShouldBe(AssistantKind.Dpm);
        sdk.ContextSdkVersion.ShouldBe("3.6.0");
        sdk.CoordinatorVersion.ShouldNotBeNull().ShouldStartWith("3.6.");
        sdk.SupportsMultiIde.ShouldBe(true);
        sdk.Notes.ShouldBeEmpty();
    }
}
