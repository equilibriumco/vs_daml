using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Sdk;

public class SdkResolverTests
{
    private const string Dpm = @"C:\Users\ann\AppData\Roaming\dpm\bin\dpm.cmd";
    private const string Daml = @"C:\Users\ann\.daml\bin\daml.cmd";
    private static readonly string[] DpmVersions = ["versions", "-o", "json"];
    private static readonly string[] DamlVersion = ["version"];

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly Dictionary<string, string> _environment = new()
    {
        ["APPDATA"] = @"C:\Users\ann\AppData\Roaming",
        ["USERPROFILE"] = @"C:\Users\ann",
    };

    private SdkResolver Resolver() => new(
        _runner,
        new AssistantLocator(_files, name => _environment.TryGetValue(name, out var value) ? value : null),
        new YamlManifestReader(_files));

    private void Package(string root, string sdkVersion) =>
        _files.AddFile(root + @"\daml.yaml", $"sdk-version: {sdkVersion}\nname: {root.Split('\\').Last()}\nversion: 1.0.0\nsource: daml\n");

    private static WorkspaceContext Ide(string packageRoot) => new()
    {
        WorkspaceRoot = packageRoot,
        ServerWorkingDirectory = packageRoot,
        Mode = ServerMode.Ide,
        PackageRoot = packageRoot,
        Packages = [packageRoot],
        Version = 1,
    };

    private static string ActiveJson(string active, params string[] others) =>
        "[" + string.Join(",", others.Select(v => $$"""{"version":"{{v}}","installed":true}""")
            .Append($$"""{"version":"{{active}}","installed":true,"active":true}""")) + "]";

    private Task<SdkResolution> ResolveAsync(WorkspaceContext workspace, LanguageServerOptions? options = null) =>
        Resolver().ResolveAsync(workspace, options ?? new LanguageServerOptions(), CancellationToken.None);

    [Fact, Trait("Category", "Unit")]
    public async Task Dpm_with_an_active_sdk_runs_the_package()
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\app", "3.6.0");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));

        var sdk = await ResolveAsync(Ide(@"C:\repo\app"));

        sdk.Assistant.ShouldBe(new AssistantLocation(AssistantKind.Dpm, Dpm));
        sdk.ActiveDpmVersion.ShouldBe("3.6.0");
        sdk.ContextSdkVersion.ShouldBe("3.6.0");
        var package = sdk.Packages.ShouldHaveSingleItem();
        package.PackageRoot.ShouldBe(@"C:\repo\app");
        package.Name.ShouldBe("app");
        package.SdkVersion.ShouldBe("3.6.0");
        sdk.Notes.ShouldBeEmpty();
        var call = _runner.Calls.ShouldHaveSingleItem();
        call.WorkingDirectory.ShouldBe(@"C:\repo\app");
        call.Timeout.ShouldNotBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_active_dpm_sdk_is_reported_but_not_taken_as_the_package_sdk()
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\app", "3.6.0");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.5.2", "3.6.0", "3.4.1"));

        var sdk = await ResolveAsync(Ide(@"C:\repo\app"));

        sdk.Assistant.Kind.ShouldBe(AssistantKind.Dpm);
        sdk.ActiveDpmVersion.ShouldBe("3.5.2");
        sdk.ContextSdkVersion.ShouldBe("3.6.0");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_package_before_sdk_3_4_runs_through_the_daml_assistant()
    {
        _files.AddFile(Dpm, "");
        _files.AddFile(Daml, "");
        Package(@"C:\repo\old", "3.3.0");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));
        _runner.On(Daml, DamlVersion, "SDK versions:\n  3.3.0  (project SDK version from daml.yaml)\n");

        var sdk = await ResolveAsync(Ide(@"C:\repo\old"));

        sdk.Assistant.ShouldBe(new AssistantLocation(AssistantKind.DamlAssistant, Daml));
        sdk.AssistantSdkVersion.ShouldBe("3.3.0");
        _runner.Calls[^1].WorkingDirectory.ShouldBe(@"C:\repo\old");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_package_before_sdk_3_4_without_the_daml_assistant_says_what_to_install()
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\old", "3.3.0");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));

        var error = await Should.ThrowAsync<SdkResolutionException>(() => ResolveAsync(Ide(@"C:\repo\old")));

        error.Code.ShouldBe("DAML0101");
        error.Message.ShouldContain("3.3.0");
        error.Message.ShouldContain("daml assistant");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task No_tools_at_all_is_DAML0101_with_instructions()
    {
        Package(@"C:\repo\app", "3.6.0");

        var error = await Should.ThrowAsync<SdkResolutionException>(() => ResolveAsync(Ide(@"C:\repo\app")));

        error.Code.ShouldBe("DAML0101");
        error.Message.ShouldContain("dpm");
        error.Message.ShouldContain("Tools > Options > Daml");
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Only_the_daml_assistant_cannot_run_a_package_from_sdk_3_4_on()
    {
        _files.AddFile(Daml, "");
        Package(@"C:\repo\app", "3.6.0");

        var error = await Should.ThrowAsync<SdkResolutionException>(() => ResolveAsync(Ide(@"C:\repo\app")));

        error.Code.ShouldBe("DAML0101");
        error.Message.ShouldContain("3.6.0");
        error.Message.ShouldContain("dpm");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Only_the_daml_assistant_is_used_when_the_workspace_does_not_pin_an_sdk()
    {
        _files.AddFile(Daml, "");
        Package(@"C:\repo\old", "2.10.0");
        var workspace = Ide(@"C:\repo\old") with { ServerWorkingDirectory = @"C:\repo", Mode = ServerMode.MultiIde };
        _runner.On(Daml, DamlVersion, "SDK versions:\n  2.10.0  (default SDK version for new projects)\n");

        var sdk = await ResolveAsync(workspace);

        sdk.Assistant.Kind.ShouldBe(AssistantKind.DamlAssistant);
        sdk.ContextSdkVersion.ShouldBeNull();
        sdk.AssistantSdkVersion.ShouldBe("2.10.0");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Configured_paths_are_used_instead_of_searching()
    {
        _files.AddFile(Dpm, "");
        _files.AddFile(@"D:\pinned\dpm.exe", "");
        Package(@"C:\repo\app", "3.6.0");
        _runner.On(@"D:\pinned\dpm.exe", DpmVersions, ActiveJson("3.6.0"));

        var sdk = await ResolveAsync(Ide(@"C:\repo\app"), new LanguageServerOptions { DpmPath = @"D:\pinned\dpm.exe" });

        sdk.Assistant.Path.ShouldBe(@"D:\pinned\dpm.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Mixed_package_sdks_are_reported_rather_than_picking_one()
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\a", "3.6.0");
        Package(@"C:\repo\b", "3.5.2");
        _files.AddFile(@"C:\repo\multi-package.yaml", "packages:\n  - a\n  - b\n");
        var workspace = Ide(@"C:\repo\a") with { ServerWorkingDirectory = @"C:\repo", Mode = ServerMode.MultiIde, Packages = [@"C:\repo\a", @"C:\repo\b"] };
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));

        var sdk = await ResolveAsync(workspace);

        sdk.ContextSdkVersion.ShouldBeNull();
        sdk.HasMixedSdkVersions.ShouldBeTrue();
        sdk.Packages.Select(p => p.SdkVersion).ShouldBe(["3.6.0", "3.5.2"]);
        sdk.Notes.ShouldContain(n => n.Contains("3.5.2") && n.Contains("3.6.0"));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_snapshot_sdk_is_flagged_as_unknown_instead_of_assumed_latest()
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\app", "0.0.0");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));

        var sdk = await ResolveAsync(Ide(@"C:\repo\app"));

        sdk.Assistant.Kind.ShouldBe(AssistantKind.Dpm);
        sdk.ContextSdkVersion.ShouldBe("0.0.0");
        sdk.Notes.ShouldContain(n => n.Contains("0.0.0"));
    }

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(BrokenDpmVersions))]
    public async Task A_failing_version_listing_is_noted_and_dpm_is_still_used(object answer, string expectedInNote)
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\app", "3.6.0");
        switch (answer)
        {
            case ProcessResult result:
                _runner.On(Dpm, DpmVersions, result);
                break;
            case Exception exception:
                _runner.On(Dpm, DpmVersions, exception);
                break;
        }

        var sdk = await ResolveAsync(Ide(@"C:\repo\app"));

        sdk.Assistant.Kind.ShouldBe(AssistantKind.Dpm);
        sdk.ActiveDpmVersion.ShouldBeNull();
        sdk.Notes.ShouldContain(n => n.Contains(expectedInNote));
    }

    public static TheoryData<object, string> BrokenDpmVersions => new()
    {
        { new ProcessResult(1, "", "Error: cache is locked"), "cache is locked" },
        { new ProcessResult(0, "not json", ""), "not json" },
        { new TimeoutException("dpm.cmd didn't finish within 60 s"), "didn't finish" },
    };
}
