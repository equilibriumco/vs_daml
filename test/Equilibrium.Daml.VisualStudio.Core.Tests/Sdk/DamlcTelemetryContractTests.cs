using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.Core.Sdk;
using Equilibrium.Daml.VisualStudio.Core.Server;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Sdk;

// damlc ide takes three switches for its usage reporting that its help doesn't list; multi-ide
// takes none (it ignores whatever it doesn't know). The extension passes none of them. When a
// test here fails, damlc changed how telemetry is switched and the decision needs a new look.
public sealed class DamlcTelemetryContractTests : IDisposable
{
    private static readonly string[] TelemetrySwitches = ["--telemetry", "--optOutTelemetry", "--telemetry-ignored"];

    private readonly ProcessRunner _runner = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-telemetry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("3.6.0")]
    [InlineData("3.5.2")]
    public async Task Damlc_ide_takes_the_telemetry_switches_without_listing_them(string sdk)
    {
        var package = Package(sdk);

        var help = await DamlcAsync(package, "ide", "--help");
        help.ExitCode.ShouldBe(0, help.Stderr);
        help.Stdout.ShouldContain("Start the Daml language server");
        help.Stdout.ShouldNotContain("telemetry", Case.Insensitive);

        // A switch damlc ide doesn't know is refused, so taking these means it knows them.
        var unknown = await DamlcAsync(package, "ide", "--no-such-switch", "--help");
        unknown.ExitCode.ShouldNotBe(0);
        unknown.Stderr.ShouldContain("Invalid option");
        foreach (var telemetry in TelemetrySwitches)
        {
            var result = await DamlcAsync(package, "ide", telemetry, "--help");
            result.ExitCode.ShouldBe(0, $"damlc {sdk} ide no longer takes {telemetry}: {result.Stderr}");
        }
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("3.6.0")]
    [InlineData("3.5.2")]
    public async Task Damlc_multi_ide_has_no_telemetry_switch(string sdk)
    {
        var help = await DamlcAsync(Package(sdk), "multi-ide", "--help");

        help.ExitCode.ShouldBe(0, help.Stderr);
        help.Stdout.ShouldContain("Multi-IDE");
        help.Stdout.ShouldNotContain("telemetry", Case.Insensitive);
    }

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("3.6.0")]
    [InlineData("3.5.2")]
    public async Task The_server_command_for_a_real_package_carries_no_telemetry_switch(string sdk)
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var package = Package(sdk);
        var files = new PhysicalFileSystem();
        foreach (var mode in new[] { ServerMode.Ide, ServerMode.MultiIde })
        {
            var workspace = new WorkspaceLocator(files, new YamlManifestReader(files))
                .Resolve(package, Path.Combine(package, "daml", "Main.daml"), mode, 1)
                .Context.ShouldNotBeNull();
            var resolution = await new SdkResolver(_runner, new AssistantLocator(), new YamlManifestReader(files))
                .ResolveAsync(workspace, new LanguageServerOptions { DpmPath = dpm }, CancellationToken.None);

            var command = LanguageServerCommandBuilder.Build(resolution, workspace, new LanguageServerOptions { DpmPath = dpm });

            resolution.ContextSdkVersion.ShouldBe(sdk);
            command.Arguments.ShouldAllBe(argument => argument.IndexOf("telemetry", StringComparison.OrdinalIgnoreCase) < 0);
        }
    }

    // A package pinned to the SDK, so dpm runs that SDK's damlc in it.
    private string Package(string sdk)
    {
        var folder = Path.Combine(_root, sdk);
        Directory.CreateDirectory(Path.Combine(folder, "daml"));
        File.WriteAllText(Path.Combine(folder, "daml.yaml"), $"sdk-version: {sdk}\nname: telemetry-probe\nversion: 0.0.1\nsource: daml\ndependencies:\n  - daml-prim\n  - daml-stdlib\n");
        File.WriteAllText(Path.Combine(folder, "daml", "Main.daml"), "module Main where\n");
        return folder;
    }

    private Task<ProcessResult> DamlcAsync(string package, params string[] arguments)
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        return _runner.RunAsync(
            new ProcessSpec(dpm, ["damlc", .. arguments]) { WorkingDirectory = package, Timeout = TimeSpan.FromMinutes(2) },
            CancellationToken.None);
    }
}
