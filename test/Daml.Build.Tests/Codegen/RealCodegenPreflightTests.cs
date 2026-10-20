using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Locking;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

// The preflight with the dpm and JDK installed on this machine and the real pinned generator,
// kept in the private cache every build on this machine uses.
public sealed class RealCodegenPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-real-preflight-" + Guid.NewGuid().ToString("N"));

    public RealCodegenPreflightTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Dar)!);
        File.WriteAllText(Dar, "stands in for a DAR: the preflight only hashes it");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Dar => Path.Combine(_root, @"Contracts\.daml\dist\contracts-1.2.0.dar");

    private static string Locks => LockNames.Root(Environment.GetEnvironmentVariable);

    private CodegenRequest Request(string dpm, string java, bool offline, bool install)
    {
        var reference = new GenerationReference(DamlReferenceSource.DamlReference, @"..\Contracts\Contracts.msbuildproj",
            Path.Combine(_root, @"Contracts\Contracts.msbuildproj"), null, "0123456789abcdef", "Contracts-0123456789abcdef");
        return new CodegenRequest(
            Path.Combine(_root, @"App\App.csproj"), "Debug", "net10.0",
            new ToolOptions(dpm, offline, install, SkipPreflight: false, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5)),
            new CodegenOptions(true, ReleaseTools.CodegenComponent, GeneratedOutputMode.Intermediate, Path.Combine(_root, @"App\obj\Debug\net10.0\Daml"),
                JavaHome: Path.GetDirectoryName(Path.GetDirectoryName(java))),
            [new CodegenInput(reference, Dar)],
            DesignTime: false,
            RuntimeVersion: "0.6.0") { LedgerAbstractionsVersion = "0.6.0" };
    }

    private static CodegenPreflight Preflight(string codegenRoot) =>
        new(new ProcessRunner(), new PhysicalFileSystem(), Environment.GetEnvironmentVariable, codegenRoot, Locks);

    private static string SharedCodegenRoot => GeneratorCache.DefaultRoot(Environment.GetEnvironmentVariable);

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task The_installed_tools_and_the_pinned_generator_pass_the_preflight()
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);

        // Installs the generator once on a machine that hasn't got it yet.
        var plan = await Preflight(SharedCodegenRoot).PlanAsync(Request(dpm, java, offline: false, install: true), CancellationToken.None);

        plan.Problems.ShouldBeEmpty(string.Join("\n", plan.Problems.Select(p => $"{p.Code}: {p.Message}")));
        var tools = plan.Tools.ShouldNotBeNull();
        Version.Parse(tools.DpmVersion).ShouldBeGreaterThanOrEqualTo(ReleaseTools.MinimumDpm);
        tools.Cache.IsMarkedReady.ShouldBeTrue();
        tools.Cache.Check(hashFiles: true).Status.ShouldBe(GeneratorCacheStatus.Ready);
        plan.Generations.Single().State.Status.ShouldBe(GenerationStatus.Stale);
    }

    // Preinstalled once, the generator serves offline builds without any download.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task After_a_preinstall_an_offline_build_passes_the_preflight()
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        (await Preflight(SharedCodegenRoot).PlanAsync(Request(dpm, java, offline: false, install: true), CancellationToken.None)).Problems.ShouldBeEmpty();

        var plan = await Preflight(SharedCodegenRoot).PlanAsync(Request(dpm, java, offline: true, install: false), CancellationToken.None);

        plan.Problems.ShouldBeEmpty(string.Join("\n", plan.Problems.Select(p => $"{p.Code}: {p.Message}")));
        plan.Tools.ShouldNotBeNull();
    }

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task Offline_without_the_generator_is_DAML0107_at_once()
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        var clock = Stopwatch.StartNew();

        var plan = await Preflight(Path.Combine(_root, "empty-codegen")).PlanAsync(Request(dpm, java, offline: true, install: false), CancellationToken.None);

        plan.Problems.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.MissingToolOffline);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }
}
