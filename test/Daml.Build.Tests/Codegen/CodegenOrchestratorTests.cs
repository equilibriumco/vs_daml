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
        runtime);

    private Task<CodegenResult> Run(CodegenRequest request)
    {
        Func<string, string?> environment = name => _environment.TryGetValue(name, out var value) ? value : null;
        var locks = Path.Combine(_root, "locks");
        return new CodegenOrchestrator(
                new CodegenPreflight(_runner, new PhysicalFileSystem(), environment, _generator.Root, locks, new ToolVersionCache()),
                new GenerationUpdate(new GeneratorRun(_runner, environment), locks))
            .RunAsync(request, CancellationToken.None);
    }

    // The generator for one reference, writing what a test gives it into its staging folder.
    private void Generates(GenerationReference reference, params string[] files)
    {
        var staging = Path.Combine(Root, GenerationPublisher.WorkFolderName, reference.GenerationId + ".new");
        _runner.On(Dpm, ["codegen-cs", "--dar", Dar, "--out", staging, "--", "-n", "Contracts"], _ =>
        {
            foreach (var file in files)
            {
                DeepPath.Write(staging, file, "class " + Path.GetFileNameWithoutExtension(file).Replace('.', '_') + " { }");
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
    }

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
