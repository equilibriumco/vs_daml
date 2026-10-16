using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Processes;
using Daml.Build.Core.Tools;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public sealed class CodegenPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-preflight-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private readonly SyntheticGeneratorCache _generator;
    private readonly Dictionary<string, string> _environment = [];

    public CodegenPreflightTests()
    {
        _generator = SyntheticGeneratorCache.Create(Path.Combine(_root, "codegen"));
        new GeneratorCache(_generator.Root, _generator.Pin).MarkReady();
        Directory.CreateDirectory(Path.GetDirectoryName(Dpm)!);
        File.WriteAllText(Dpm, "");
        Directory.CreateDirectory(Path.GetDirectoryName(Java)!);
        File.WriteAllText(Java, "");
        Directory.CreateDirectory(Path.GetDirectoryName(Dar)!);
        File.WriteAllText(Dar, "a DAR");
        _environment["PATH"] = Path.GetDirectoryName(Dpm)!;
        _environment["JAVA_HOME"] = Path.Combine(_root, "jdk");
        DpmSays("version: 1.0.22\nbuild: b65a815\n");
        JavaSays("openjdk version \"21.0.12\" 2025-10-21 LTS\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Dpm => Path.Combine(_root, @"dpm\bin\dpm.exe");

    private string Java => Path.Combine(_root, @"jdk\bin\java.exe");

    private string Dar => Path.Combine(_root, @"Contracts\.daml\dist\contracts-1.2.0.dar");

    private string Project => Path.Combine(_root, @"App\App.csproj");

    private string Root => Path.Combine(_root, @"App\obj\Debug\net10.0\Daml");

    private void DpmSays(string stdout) => _runner.On(Dpm, ["--version"], stdout);

    private void JavaSays(string stderr) => _runner.On(Java, ["-version"], new ProcessResult(0, "", stderr));

    private CodegenRequest Request(Func<ToolOptions, ToolOptions>? tools = null, string? runtime = "0.6.0", string targetFramework = "net10.0", bool designTime = false)
    {
        var reference = new GenerationReference(DamlReferenceSource.DamlReference, @"..\Contracts\Contracts.msbuildproj",
            Path.Combine(_root, @"Contracts\Contracts.msbuildproj"), "Contracts", "0123456789abcdef", "Contracts-0123456789abcdef");
        var options = new ToolOptions(null, Offline: false, AllowToolInstall: false, SkipPreflight: false, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        return new CodegenRequest(
            Project, "Debug", targetFramework,
            tools?.Invoke(options) ?? options,
            new CodegenOptions(true, _generator.Pin, GeneratedOutputMode.Intermediate, Root, JavaHome: Path.Combine(_root, "jdk")),
            [new CodegenInput(reference, Dar)],
            designTime,
            runtime);
    }

    private Task<CodegenPlan> Plan(CodegenRequest request, ToolVersionCache? versions = null) =>
        new CodegenPreflight(_runner, new PhysicalFileSystem(), name => _environment.TryGetValue(name, out var value) ? value : null,
            _generator.Root, Path.Combine(_root, "locks"), versions ?? new ToolVersionCache()).PlanAsync(request, CancellationToken.None);

    private string Fingerprint(string dpmVersion = "1.0.22") =>
        GenerationFingerprint.Compute(new GenerationFingerprintInputs("0123456789abcdef", Hashing.Sha256File(Dar), _generator.Pin.Digest, dpmVersion));

    [Fact, Trait("Category", "Unit")]
    public async Task With_everything_in_place_each_input_gets_its_folder_fingerprint_and_state()
    {
        var plan = await Plan(Request());

        plan.Problems.ShouldBeEmpty();
        var generation = plan.Generations.ShouldHaveSingleItem();
        generation.Folder.ShouldBe(Path.Combine(Root, "Contracts-0123456789abcdef"));
        generation.Fingerprint.ShouldBe(Fingerprint());
        (generation.State.Status, generation.State.Reason).ShouldBe((GenerationStatus.Stale, "There is no generation yet."));
        plan.Tools.ShouldNotBeNull();
        (plan.Tools.Dpm, plan.Tools.Java, plan.Tools.DpmVersion).ShouldBe((Dpm, Java, "1.0.22"));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_generation_published_from_the_same_inputs_is_current()
    {
        var folder = Path.Combine(Root, "Contracts-0123456789abcdef");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Asset.cs"), "partial record Asset");
        new GenerationManifest("0123456789abcdef", Fingerprint(), Project, DamlReferenceKind.Project, "x", "Contracts", Dar, "da", _generator.Pin.Reference,
            [new GenerationManifestFile("Asset.cs", Hashing.Sha256Text("partial record Asset"))]).Write(Path.Combine(folder, GenerationManifest.FileName));

        var plan = await Plan(Request());

        plan.Generations.Single().State.Status.ShouldBe(GenerationStatus.Current);
    }

    // Checked before anything is downloaded: a project that can't compile the code gets nothing installed.
    [Fact, Trait("Category", "Unit")]
    public async Task Every_problem_of_the_project_and_its_tools_is_reported_before_the_generator_is_touched()
    {
        DpmSays("version: 1.0.10\n");
        JavaSays("java version \"11.0.2\"\n");
        Directory.Delete(_generator.Home, recursive: true);

        var plan = await Plan(Request(tools: t => t with { AllowToolInstall = true }, runtime: null, targetFramework: "net48"));

        plan.Problems.Select(p => p.Code).ShouldBe(
            [DamlDiagnosticCodes.RuntimeMismatch, DamlDiagnosticCodes.RuntimeMismatch, DamlDiagnosticCodes.DpmTooOld, DamlDiagnosticCodes.MissingJava],
            ignoreOrder: true);
        plan.Generations.ShouldBeEmpty();
        _runner.Calls.ShouldNotContain(c => c.Arguments.SequenceEqual(new[] { "install", "package" }));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_generator_it_may_not_install_stops_the_plan()
    {
        Directory.Delete(_generator.Home, recursive: true);

        var plan = await Plan(Request());

        plan.Problems.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.MissingTool);
        plan.Generations.ShouldBeEmpty();
    }

    // Skipping the preflight skips asking the tools, so the fingerprint can't know the dpm.
    [Fact, Trait("Category", "Unit")]
    public async Task Skipping_the_preflight_asks_no_tool_for_its_version()
    {
        var plan = await Plan(Request(tools: t => t with { SkipPreflight = true }));

        plan.Problems.ShouldBeEmpty();
        _runner.Calls.ShouldBeEmpty();
        plan.Generations.Single().Fingerprint.ShouldBe(Fingerprint(dpmVersion: "unchecked"));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_DAR_that_isnt_there_is_DAML0104()
    {
        File.Delete(Dar);

        var plan = await Plan(Request());

        var problem = plan.Problems.ShouldHaveSingleItem();
        problem.Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
        problem.Message.ShouldBe($@"There is no DAR at {Dar} to generate from. Build ..\Contracts\Contracts.msbuildproj first.");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_folder_with_files_the_generator_didnt_write_is_DAML0108()
    {
        var folder = Path.Combine(Root, "Contracts-0123456789abcdef");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Mine.cs"), "class Mine { }");

        var plan = await Plan(Request());

        plan.Problems.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.OutputConflict);
        plan.Generations.Single().State.Status.ShouldBe(GenerationStatus.Conflict);
    }

    // A build of many projects asks each tool once, until the tool itself changes.
    [Fact, Trait("Category", "Unit")]
    public async Task Tool_versions_are_asked_once_per_file_until_it_changes()
    {
        var versions = new ToolVersionCache();

        await Plan(Request(), versions);
        await Plan(Request(), versions);
        File.SetLastWriteTimeUtc(Dpm, DateTime.UtcNow.AddMinutes(1));
        await Plan(Request(), versions);

        _runner.Calls.Count(c => c.FileName == Dpm).ShouldBe(2);
        _runner.Calls.Count(c => c.FileName == Java).ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_design_time_build_plans_nothing_and_runs_nothing()
    {
        var plan = await Plan(Request(designTime: true));

        (plan.Problems.Count, plan.Generations.Count, plan.Tools).ShouldBe((0, 0, null));
        _runner.Calls.ShouldBeEmpty();
    }
}
