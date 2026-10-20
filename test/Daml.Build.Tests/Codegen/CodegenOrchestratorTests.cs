using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Building;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Processes;
using Daml.Build.Core.Tools;
using Daml.Build.Tests.FileSystem;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public sealed class CodegenOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-orchestrator-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private readonly SyntheticGeneratorCache _generator;
    private readonly Dictionary<string, string> _environment = [];

    public CodegenOrchestratorTests()
    {
        _generator = SyntheticGeneratorCache.Create(Path.Combine(_root, "codegen"));
        new GeneratorCache(_generator.Root, _generator.Pin).MarkReady();
        foreach (var tool in new[] { Dpm, Java })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
            File.WriteAllText(tool, "");
        }
        TestDar.Write(Dar);
        _environment["PATH"] = Path.GetDirectoryName(Dpm)!;
        _environment["JAVA_HOME"] = Path.Combine(_root, "jdk");
        _runner.On(Dpm, ["--version"], "version: 1.0.22\nbuild: b65a815\n");
        _runner.On(Java, ["-version"], new ProcessResult(0, "", "openjdk version \"21.0.12\" 2025-10-21 LTS\n"));
    }

    public void Dispose() => DeepPath.Delete(_root);

    private string Dpm => Path.Combine(_root, @"dpm\bin\dpm.exe");

    private string Java => Path.Combine(_root, @"jdk\bin\java.exe");

    private string Dar => Path.Combine(_root, @"Contracts\.daml\dist\contracts-1.2.0.dar");

    private string Project => Path.Combine(_root, @"App\App.csproj");

    private string Root => Path.Combine(_root, @"App\obj\Debug\net10.0\Daml");

    private GenerationReference Contracts => Reference("Contracts", "0123456789abcdef");

    private GenerationReference Reference(string name, string id) => new(
        DamlReferenceSource.DamlReference, $@"..\{name}\{name}.msbuildproj", Path.Combine(_root, $@"{name}\{name}.msbuildproj"), "Contracts", id, $"{name}-{id}");

    private CodegenRequest Request(bool designTime = false, string? runtime = "0.6.0", params GenerationReference[] references) => new(
        Project,
        "Debug",
        "net10.0",
        new ToolOptions(null, Offline: false, AllowToolInstall: false, SkipPreflight: false, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5)),
        new CodegenOptions(true, _generator.Pin, GeneratedOutputMode.Intermediate, Root, JavaHome: Path.Combine(_root, "jdk")),
        (references.Length == 0 ? [Contracts] : references).Select(r => new CodegenInput(r, Dar)).ToList(),
        designTime,
        runtime) { LedgerAbstractionsVersion = "0.6.0" };

    // The same project in another configuration, target framework or output mode.
    private CodegenRequest Request(string configuration, string targetFramework, GeneratedOutputMode mode, string root) => Request() with
    {
        Configuration = configuration,
        TargetFramework = targetFramework,
        Codegen = Request().Codegen with { OutputMode = mode, GeneratedRoot = root },
    };

    private CodegenOrchestrator Orchestrator()
    {
        Func<string, string?> environment = name => _environment.TryGetValue(name, out var value) ? value : null;
        var locks = Path.Combine(_root, "locks");
        return new CodegenOrchestrator(
            new CodegenPreflight(_runner, new PhysicalFileSystem(), environment, _generator.Root, locks, new ToolVersionCache()),
            new GenerationUpdate(new GeneratorRun(_runner, environment), locks));
    }

    private Task<CodegenResult> Run(CodegenRequest request) => Orchestrator().RunAsync(request, CancellationToken.None);

    // The generator for one reference, writing what a test gives it into its staging folder.
    private void Generates(GenerationReference reference, params string[] files) => GeneratesInto(Root, reference, null, files);

    // The same, into another root, doing whileRunning first, as a generator that takes its time.
    private void GeneratesInto(string root, GenerationReference reference, Func<Task>? whileRunning, params string[] files)
    {
        var staging = Path.Combine(root, GenerationPublisher.WorkFolderName, reference.GenerationId + ".new");
        _runner.On(Dpm, ["codegen-cs", "--dar", Dar, "--out", staging, "--", "-n", reference.Namespace!], async _ =>
        {
            if (whileRunning is not null)
            {
                await whileRunning();
            }
            foreach (var file in files)
            {
                DeepPath.Write(staging, file, "class " + Path.GetFileNameWithoutExtension(file).Replace('.', '_') + " { }");
            }
            return new ProcessResult(0, "", "");
        });
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private IEnumerable<ProcessSpec> GeneratorCalls => _runner.Calls.Where(c => c.Arguments.Count > 0 && c.Arguments[0] == "codegen-cs");

    private static IEnumerable<BuildMessage> Errors(CodegenResult result) => result.Messages.Where(m => m.Severity == DamlDiagnosticSeverity.Error);

    [Fact, Trait("Category", "Unit")]
    public async Task A_real_build_generates_and_hands_the_files_to_the_compiler()
    {
        Generates(Contracts, @"Contracts\Asset\Asset.cs", @"Contracts\Asset\Asset.Give.cs");

        var result = await Run(Request());

        Errors(result).ShouldBeEmpty();
        result.Files.Select(f => (f.Path, f.GenerationId)).ShouldBe(
        [
            (Path.Combine(Root, @"Contracts-0123456789abcdef\Contracts\Asset\Asset.cs"), "0123456789abcdef"),
            (Path.Combine(Root, @"Contracts-0123456789abcdef\Contracts\Asset\Asset.Give.cs"), "0123456789abcdef"),
        ]);
        result.Messages.ShouldAllBe(m => m.File == Project);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_manifest_paths_never_become_compile_items(bool designTime)
    {
        Generates(Contracts, @"Contracts\Asset.cs");
        await Run(Request());
        var folder = Path.Combine(Root, Contracts.FolderName);
        var manifest = Path.Combine(folder, GenerationManifest.FileName);
        File.AppendAllText(manifest, "file=bad ..\\outside.cs\n");
        var calls = GeneratorCalls.Count();
        var result = await Run(Request(designTime: designTime));
        result.Files.ShouldBeEmpty();
        File.Exists(Path.Combine(folder, @"Contracts\Asset.cs")).ShouldBeTrue();
        GeneratorCalls.Count().ShouldBe(calls);
        result.Messages.ShouldNotBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task What_the_preflight_finds_stops_the_build_before_the_generator()
    {
        var result = await Run(Request(runtime: null));

        Errors(result).ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.RuntimeMismatch);
        GeneratorCalls.ShouldBeEmpty();
        result.Files.ShouldBeEmpty();
    }

    // A project with nothing to generate from needs neither dpm nor Java.
    [Fact, Trait("Category", "Unit")]
    public async Task Without_references_nothing_is_checked_or_run()
    {
        var request = Request() with { Inputs = [] };

        var result = await Run(request);

        (result.Files.Count, result.Messages.Count, _runner.Calls.Count).ShouldBe((0, 0, 0));
    }

    // The project dropped its last Daml reference: what it generated goes, still without any tool.
    [Fact, Trait("Category", "Unit")]
    public async Task Without_references_a_real_build_takes_away_what_earlier_builds_generated()
    {
        Generates(Contracts, @"Contracts\Asset\Asset.cs");
        await Run(Request());
        var calls = _runner.Calls.Count;

        var result = await Run(Request() with { Inputs = [] });

        result.Files.ShouldBeEmpty();
        Errors(result).ShouldBeEmpty();
        Directory.Exists(Root).ShouldBeFalse();
        _runner.Calls.Count.ShouldBe(calls);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_references_design_time_touches_nothing()
    {
        Generates(Contracts, @"Contracts\Asset\Asset.cs");
        var published = await Run(Request());

        var result = await Run(Request(designTime: true) with { Inputs = [] });

        (result.Files.Count, result.Messages.Count).ShouldBe((0, 0));
        File.Exists(published.Files.Single().Path).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Clean_takes_away_every_generation_of_the_project()
    {
        Generates(Contracts, @"Contracts\Asset\Asset.cs");
        await Run(Request());
        Func<string, string?> environment = _ => null;
        var locks = Path.Combine(_root, "locks");
        var orchestrator = new CodegenOrchestrator(
            new CodegenPreflight(_runner, new PhysicalFileSystem(), environment, _generator.Root, locks, new ToolVersionCache()),
            new GenerationUpdate(new GeneratorRun(_runner, environment), locks));

        var result = await orchestrator.CleanAsync(Request(), CancellationToken.None);

        Errors(result).ShouldBeEmpty();
        Directory.Exists(Root).ShouldBeFalse();
    }

    // Builds started together, as two command lines or an IDE and a command line may: the root's
    // lock lets one generate; the other then finds the generation current and compiles it.
    [Fact, Trait("Category", "Unit")]
    public async Task Two_builds_of_the_same_project_at_once_run_the_generator_once()
    {
        using var started = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        GeneratesInto(Root, Contracts, async () => { started.Release(); await go.WaitAsync(); }, @"Contracts\Asset.cs");

        var first = Run(Request());
        (await started.WaitAsync(Patience)).ShouldBeTrue("the first build's generator should be running");
        var second = Run(Request());
        go.Release();
        var results = await Task.WhenAll(first, second);

        GeneratorCalls.Count().ShouldBe(1);
        results.SelectMany(Errors).ShouldBeEmpty();
        results[1].Files.ShouldBe(results[0].Files);
        // Only what a manifest lists is compiled; nothing from the work folder ever is.
        results.SelectMany(r => r.Files).ShouldNotContain(f => f.Path.Contains(GenerationPublisher.WorkFolderName));
    }

    // Source mode: Debug and Release share one folder and one fingerprint.
    [Fact, Trait("Category", "Unit")]
    public async Task Debug_and_Release_generating_into_the_sources_at_once_share_one_generation()
    {
        var source = Path.Combine(_root, @"App\Generated\Daml");
        using var started = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        GeneratesInto(source, Contracts, async () => { started.Release(); await go.WaitAsync(); }, @"Contracts\Asset.cs");

        var debug = Run(Request("Debug", "net10.0", GeneratedOutputMode.Source, source));
        (await started.WaitAsync(Patience)).ShouldBeTrue("the Debug build's generator should be running");
        var release = Run(Request("Release", "net10.0", GeneratedOutputMode.Source, source));
        go.Release();
        var results = await Task.WhenAll(debug, release);

        GeneratorCalls.Count().ShouldBe(1);
        results.SelectMany(Errors).ShouldBeEmpty();
        results[1].Files.ShouldBe(results[0].Files);
    }

    // Each target framework has a root, and a lock, of its own: neither waits for the other.
    [Fact, Trait("Category", "Unit")]
    public async Task Two_target_frameworks_generate_side_by_side_into_folders_of_their_own()
    {
        var net10 = Path.Combine(_root, @"App\obj\Debug\net10.0\Daml");
        var net11 = Path.Combine(_root, @"App\obj\Debug\net11.0\Daml");
        using var started = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        foreach (var root in new[] { net10, net11 })
        {
            GeneratesInto(root, Contracts, async () => { started.Release(); await go.WaitAsync(); }, @"Contracts\Asset.cs");
        }

        var first = Run(Request("Debug", "net10.0", GeneratedOutputMode.Intermediate, net10));
        var second = Run(Request("Debug", "net11.0", GeneratedOutputMode.Intermediate, net11));
        (await started.WaitAsync(Patience) && await started.WaitAsync(Patience)).ShouldBeTrue("both generators should run at once");
        go.Release(2);
        var results = await Task.WhenAll(first, second);

        GeneratorCalls.Count().ShouldBe(2);
        results.SelectMany(Errors).ShouldBeEmpty();
        results[0].Files.ShouldHaveSingleItem().Path.ShouldStartWith(net10);
        results[1].Files.ShouldHaveSingleItem().Path.ShouldStartWith(net11);
    }

    // Clean waits for the build it overlaps and then removes the whole generation, never half of it.
    [Fact, Trait("Category", "Unit")]
    public async Task A_clean_during_a_build_waits_for_it_and_takes_what_it_published()
    {
        using var started = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        GeneratesInto(Root, Contracts, async () => { started.Release(); await go.WaitAsync(); }, @"Contracts\Asset.cs");

        var build = Run(Request());
        (await started.WaitAsync(Patience)).ShouldBeTrue("the build's generator should be running");
        var clean = Orchestrator().CleanAsync(Request(), CancellationToken.None);
        clean.IsCompleted.ShouldBeFalse("the clean waits for the build's lock");
        go.Release();
        var (built, cleaned) = (await build, await clean);

        Errors(built).ShouldBeEmpty();
        Errors(cleaned).ShouldBeEmpty();
        Directory.Exists(Root).ShouldBeFalse();
    }

    // A new Namespace is a new generation; the old one goes in the same build.
    [Fact, Trait("Category", "Unit")]
    public async Task Changing_the_namespace_replaces_the_generation_in_one_build()
    {
        Generates(Contracts, @"Contracts\Asset.cs");
        await Run(Request());
        var billing = Contracts with { Namespace = "Billing", GenerationId = "1111111111111111", FolderName = "Contracts-1111111111111111" };
        Generates(billing, @"Billing\Asset.cs");

        var result = await Run(Request(references: billing));

        Errors(result).ShouldBeEmpty();
        result.Files.ShouldHaveSingleItem().Path.ShouldBe(Path.Combine(Root, @"Contracts-1111111111111111\Billing\Asset.cs"));
        Directory.Exists(Path.Combine(Root, Contracts.FolderName)).ShouldBeFalse();
    }

    // Two generations writing the same file would give the compiler every type twice (CS0101).
    [Fact, Trait("Category", "Unit")]
    public async Task Two_generations_writing_the_same_files_are_DAML0202_naming_both()
    {
        var finance = Reference("Finance", "fedcba9876543210");
        Generates(Contracts, @"Contracts\Asset\Asset.cs", @"Contracts\Asset\Asset.Give.cs", @"Contracts\Own.cs");
        Generates(finance, @"Contracts\Asset\Asset.cs", @"Contracts\Asset\Asset.Give.cs", @"Contracts\Other.cs");

        var result = await Run(Request(references: [Contracts, finance]));

        var error = Errors(result).ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.TypeCollision);
        error.Text.ShouldContain(@"..\Contracts\Contracts.msbuildproj");
        error.Text.ShouldContain(@"..\Finance\Finance.msbuildproj");
        error.Text.ShouldContain(@"Contracts\Asset\Asset.cs");
        error.Text.ShouldContain("Namespace");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Design_time_lists_the_last_published_generation_and_runs_nothing()
    {
        Generates(Contracts, @"Contracts\Asset\Asset.cs");
        var published = await Run(Request());
        var calls = _runner.Calls.Count;
        File.WriteAllText(Dar, "a newer DAR the next real build will see");

        var result = await Run(Request(designTime: true));

        result.Files.ShouldBe(published.Files);
        result.Messages.ShouldBeEmpty();
        _runner.Calls.Count.ShouldBe(calls);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Design_time_before_any_real_build_says_to_build()
    {
        var result = await Run(Request(designTime: true));

        result.Files.ShouldBeEmpty();
        var message = result.Messages.ShouldHaveSingleItem();
        message.Severity.ShouldBe(DamlDiagnosticSeverity.Information);
        message.Text.ShouldContain("build the project");
        Directory.Exists(Root).ShouldBeFalse();
    }

    // Recovering is a real build's job; design-time only says one is needed, and touches nothing.
    [Fact, Trait("Category", "Unit")]
    public async Task Design_time_after_an_unfinished_publish_says_so_and_leaves_it_for_the_real_build()
    {
        Generates(Contracts, @"Contracts\Asset\Asset.cs");
        var published = await Run(Request());
        var journal = Path.Combine(Root, GenerationPublisher.WorkFolderName, "0123456789abcdef.journal");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "staging=0123456789abcdef.new\nphase=new-moved\n");

        var result = await Run(Request(designTime: true));

        result.Files.ShouldBe(published.Files);
        result.Messages.ShouldHaveSingleItem().Text.ShouldContain("unfinished");
        File.Exists(journal).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Design_time_doesnt_list_another_projects_generation()
    {
        Generates(Contracts, @"Contracts\Asset\Asset.cs");
        await Run(Request());

        var result = await Run(Request(designTime: true) with { ProjectPath = Path.Combine(_root, @"Other\Other.csproj") });

        result.Files.ShouldBeEmpty();
        result.Messages.ShouldHaveSingleItem().Text.ShouldContain(Project);
    }
}
