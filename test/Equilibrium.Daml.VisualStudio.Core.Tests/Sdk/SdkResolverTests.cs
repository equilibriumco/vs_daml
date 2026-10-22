using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.Core.Sdk;
using Equilibrium.Daml.VisualStudio.Core.Server;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Sdk;

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

    private static readonly string[] DamlcHelp = ["damlc", "--help"];
    private static readonly string[] MultiIdeHelp = ["damlc", "multi-ide", "--help"];

    public SdkResolverTests()
    {
        // Every tool answers the capability probe like damlc 3.6.2 unless a test says otherwise.
        foreach (var tool in new[] { Dpm, Daml, @"D:\pinned\dpm.exe" })
        {
            _runner.On(tool, DamlcHelp, DamlcProbeTests.TopHelp);
            _runner.On(tool, MultiIdeHelp, DamlcProbeTests.MultiIdeHelp);
        }
    }

    private SdkResolver Resolver() => new(
        _runner,
        new AssistantLocator(_files, name => _environment.TryGetValue(name, out var value) ? value : null),
        new YamlManifestReader(_files, name => _environment.TryGetValue(name, out var value) ? value : null),
        name => _environment.TryGetValue(name, out var value) ? value : null);

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
    public async Task The_context_inherits_the_multi_package_SDK_and_honors_environment_overrides()
    {
        _files.AddFile(Dpm, "");
        _files.AddFile(@"C:\repo\app\daml.yaml", "name: app\nsource: daml\n");
        _files.AddFile(@"C:\repo\multi-package.yaml", "sdk-version: 3.5.2\n");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));
        (await ResolveAsync(Ide(@"C:\repo\app"))).ContextSdkVersion.ShouldBe("3.5.2");

        _files.AddFile(@"C:\external\multi-package.yaml", "sdk-version: 3.6.0\n");
        _environment["DPM_MULTI_PACKAGE"] = @"C:\external";
        (await ResolveAsync(Ide(@"C:\repo\app"))).ContextSdkVersion.ShouldBe("3.6.0");
        _environment["DPM_SDK_VERSION"] = "3.5.2";
        (await ResolveAsync(Ide(@"C:\repo\app"))).ContextSdkVersion.ShouldBe("3.5.2");
    }

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
        sdk.CoordinatorVersion.ShouldBe("3.6.2");
        sdk.SupportsMultiIde.ShouldBe(true);
        sdk.SupportsIdeIdentifier.ShouldBe(true);
        _runner.Calls.Select(c => string.Join(" ", c.Arguments)).ShouldBe(["versions -o json", "damlc --help", "damlc multi-ide --help"]);
        _runner.Calls.ShouldAllBe(c => c.WorkingDirectory == @"C:\repo\app" && c.Timeout != null);
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
        var damlCalls = _runner.Calls.Where(c => c.FileName == Daml).ToList();
        damlCalls.Select(c => string.Join(" ", c.Arguments)).ShouldBe(["version", "damlc --help", "damlc multi-ide --help"]);
        damlCalls.ShouldAllBe(c => c.WorkingDirectory == @"C:\repo\old");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_package_before_sdk_3_4_without_the_daml_assistant_says_what_to_install()
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\old", "3.3.0");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));

        var error = await Should.ThrowAsync<SdkResolutionException>(() => ResolveAsync(Ide(@"C:\repo\old")));

        error.Code.ShouldBe("EQD0101");
        error.Message.ShouldContain("3.3.0");
        error.Message.ShouldContain("daml assistant");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task No_tools_at_all_is_DAML0101_with_instructions()
    {
        Package(@"C:\repo\app", "3.6.0");

        var error = await Should.ThrowAsync<SdkResolutionException>(() => ResolveAsync(Ide(@"C:\repo\app")));

        error.Code.ShouldBe("EQD0101");
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

        error.Code.ShouldBe("EQD0101");
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

    [Fact, Trait("Category", "Unit")]
    public async Task A_failing_probe_falls_back_to_the_sdk_version_as_a_hint()
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\app", "3.6.0");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));
        _runner.On(Dpm, DamlcHelp, new ProcessResult(1, "", "damlc crashed"));

        var sdk = await ResolveAsync(Ide(@"C:\repo\app"));

        sdk.CoordinatorVersion.ShouldBeNull();
        sdk.SupportsMultiIde.ShouldBe(true);
        sdk.SupportsIdeIdentifier.ShouldBeNull();
        sdk.Notes.ShouldContain(n => n.Contains("damlc crashed"));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failing_probe_on_a_snapshot_sdk_leaves_the_capabilities_unknown()
    {
        _files.AddFile(Dpm, "");
        Package(@"C:\repo\app", "0.0.0");
        _runner.On(Dpm, DpmVersions, ActiveJson("3.6.0"));
        _runner.On(Dpm, DamlcHelp, new TimeoutException("dpm.cmd didn't finish within 60 s"));

        var sdk = await ResolveAsync(Ide(@"C:\repo\app"));

        sdk.SupportsMultiIde.ShouldBeNull();
        sdk.SupportsIdeIdentifier.ShouldBeNull();
    }

    public static TheoryData<object, string> BrokenDpmVersions => new()
    {
        { new ProcessResult(1, "", "Error: cache is locked"), "cache is locked" },
        { new ProcessResult(0, "not json", ""), "not json" },
        { new TimeoutException("dpm.cmd didn't finish within 60 s"), "didn't finish" },
    };
}
