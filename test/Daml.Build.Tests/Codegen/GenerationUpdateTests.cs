using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using Daml.Build.Tests.FileSystem;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public sealed class GenerationUpdateTests : IDisposable
{
    private const string Id = "0123456789abcdef";
    private const string FolderName = "Contracts-0123456789abcdef";
    private const string Fingerprint = "f1n9";
    private const string Dpm = @"C:\dpm\bin\dpm.exe";
    private const string Java = @"C:\jdk-21\bin\java.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-update-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGenerator _generator = new();
    private readonly SyntheticGeneratorCache _cache;

    public GenerationUpdateTests()
    {
        _cache = SyntheticGeneratorCache.Create(Path.Combine(_root, "codegen"));
        TestDar.Write(Dar);
    }

    public void Dispose() => DeepPath.Delete(_root);

    private string Dar => Path.Combine(_root, @"Contracts\.daml\dist\contracts-1.2.0.dar");

    private string Project => Path.Combine(_root, @"App\App.csproj");

    // A Source root: every configuration of the project writes here.
    private string Root => Path.Combine(_root, @"App\Generated");

    private string Folder => Path.Combine(Root, FolderName);

    private string Work => Path.Combine(Root, GenerationPublisher.WorkFolderName);

    private string Locks => Path.Combine(_root, "locks");

    private string OutputLock => LockNames.Output(Locks, Root);

    private CodegenInput Input => new(
        new GenerationReference(DamlReferenceSource.DamlReference, @"..\Contracts\Contracts.msbuildproj",
            Path.Combine(_root, @"Contracts\Contracts.msbuildproj"), "Contracts", Id, FolderName),
        Dar);

    private CodegenRequest Request(string configuration = "Debug", TimeSpan? lockTimeout = null) => new(
        Project,
        configuration,
        "net10.0",
        new ToolOptions(null, Offline: false, AllowToolInstall: false, SkipPreflight: false, TimeSpan.FromMinutes(5), lockTimeout ?? TimeSpan.FromSeconds(5)),
        new CodegenOptions(true, _cache.Pin, GeneratedOutputMode.Source, Root, JavaHome: null),
        [Input],
        DesignTime: false,
        "0.6.0");

    // What the preflight found before the update took the lock.
    private CodegenPlan Plan(GenerationStatus found = GenerationStatus.Stale) => new(
        [],
        [new PlannedGeneration(Input, Folder, Fingerprint, new GenerationState(found, found == GenerationStatus.Current ? null : "There is no generation yet.", [], []))],
        new CodegenTools(Dpm, Java, "1.0.22", new GeneratorCache(_cache.Root, _cache.Pin)));

    private Task<CodegenResult> Update(CodegenPlan? plan = null, CodegenRequest? request = null, CancellationToken cancellationToken = default) =>
        new GenerationUpdate(new GeneratorRun(_generator, _ => null), Locks)
            .RunAsync(request ?? Request(), plan ?? Plan(), cancellationToken);

    // A generation as a build publishes it: its files and, last, its manifest.
    private void Published(string folder, string content = "class Asset { }", string fingerprint = Fingerprint)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Asset.cs"), content);
        new GenerationManifest(Id, fingerprint, Project, DamlReferenceKind.Project, Path.Combine(_root, @"Contracts\Contracts.msbuildproj"), "Contracts",
            Dar, Hashing.Sha256File(Dar), _cache.Pin.Reference, [new GenerationManifestFile("Asset.cs", Hashing.Sha256Text(content))])
            .Write(Path.Combine(folder, GenerationManifest.FileName));
    }

    // What a build killed between moving the old generation aside and moving the new one in leaves.
    private string KilledWithTheOldGenerationAside()
    {
        var staging = Path.Combine(Work, Id + ".new");
        Published(Path.Combine(Work, Id + ".old"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(Work, Id + ".journal"), $"staging={Id}.new\nphase=old-moved\n");
        return staging;
    }

    private string Content() => File.ReadAllText(Path.Combine(Folder, "Asset.cs"));

    private IEnumerable<string> WorkLeftovers() =>
        Directory.Exists(Work) ? Directory.GetFileSystemEntries(Work).Select(p => Path.GetFileName(p)!).Where(n => n != ".gitignore") : [];

    [Fact, Trait("Category", "Unit")]
    public async Task A_stale_generation_is_generated_published_and_handed_to_the_compiler()
    {
        _generator.Writes(new() { [@"Contracts\Asset.cs"] = "class Asset { }", [@"Contracts\Asset.Give.cs"] = "class Give { }" });

        var result = await Update();

        result.Messages.ShouldAllBe(m => m.Severity == DamlDiagnosticSeverity.Information);
        result.Messages.ShouldHaveSingleItem().Text.ShouldContain("There is no generation yet.");
        result.Files.ShouldBe(
        [
            new GeneratedFile(Path.Combine(Folder, @"Contracts\Asset.cs"), Id),
            new GeneratedFile(Path.Combine(Folder, @"Contracts\Asset.Give.cs"), Id),
        ]);
        GenerationState.Evaluate(Folder, Fingerprint, Project).Status.ShouldBe(GenerationStatus.Current);
        WorkLeftovers().ShouldBeEmpty();
    }

    // Our own steps take any length; the warning is for the compiler and the editor, which may not.
    [Fact, Trait("Category", "Unit")]
    public async Task Files_past_the_path_limit_are_compiled_with_a_DAML0111_warning()
    {
        _generator.Writes(new() { [DeepPath.Relative] = "class Registration { }", ["Asset.cs"] = "class Asset { }" });

        var result = await Update();

        var warning = result.Messages.Where(m => m.Severity == DamlDiagnosticSeverity.Warning).ShouldHaveSingleItem();
        warning.Code.ShouldBe(DamlDiagnosticCodes.LongGeneratedPath);
        var longest = Path.Combine(Folder, DeepPath.Relative);
        warning.Text.ShouldContain($"{longest.Length}: {longest}");
        warning.Text.ShouldContain("DamlGeneratedDir");
        result.Messages.ShouldNotContain(m => m.Severity == DamlDiagnosticSeverity.Error);
        result.Files.Select(f => f.Path).ShouldBe([Path.Combine(Folder, "Asset.cs"), longest]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_current_generation_is_compiled_as_it_is_without_running_anything()
    {
        Published(Folder);

        var result = await Update(Plan(GenerationStatus.Current));

        _generator.Calls.ShouldBeEmpty();
        result.Messages.ShouldBeEmpty();
        result.Files.ShouldBe([new GeneratedFile(Path.Combine(Folder, "Asset.cs"), Id)]);
    }

    // The preflight looked before the lock; the other build published while this one waited.
    [Fact, Trait("Category", "Unit")]
    public async Task A_generation_another_build_published_while_this_one_waited_isnt_generated_again()
    {
        var other = await FileLock.AcquireAsync(OutputLock, TimeSpan.FromSeconds(5), CancellationToken.None);
        var update = Update();
        Published(Folder);
        other.Dispose();

        var result = await update;

        _generator.Calls.ShouldBeEmpty();
        result.Messages.ShouldHaveSingleItem().Text.ShouldContain("another build");
        result.Files.ShouldBe([new GeneratedFile(Path.Combine(Folder, "Asset.cs"), Id)]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_root_locked_past_the_time_limit_is_DAML0105_and_nothing_runs()
    {
        using var other = await FileLock.AcquireAsync(OutputLock, TimeSpan.FromSeconds(5), CancellationToken.None);

        var result = await Update(request: Request(lockTimeout: TimeSpan.FromMilliseconds(300)));

        var error = result.Messages.ShouldHaveSingleItem();
        (error.Severity, error.Code).ShouldBe((DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.LockTimeout));
        error.Text.ShouldContain(Root);
        error.Text.ShouldContain("DamlLockTimeoutSeconds");
        _generator.Calls.ShouldBeEmpty();
        result.Files.ShouldBeEmpty();
    }

    // Debug was killed halfway through publishing into the Source root it shares with Release.
    [Fact, Trait("Category", "Unit")]
    public async Task A_publish_another_configuration_left_unfinished_is_undone_before_the_generation_is_judged()
    {
        KilledWithTheOldGenerationAside();

        var result = await Update(Plan(GenerationStatus.Stale), Request(configuration: "Release"));

        _generator.Calls.ShouldBeEmpty();
        result.Messages.ShouldHaveSingleItem().Text.ShouldContain("unfinished");
        result.Files.ShouldBe([new GeneratedFile(Path.Combine(Folder, "Asset.cs"), Id)]);
        WorkLeftovers().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failing_generator_leaves_the_previous_generation_in_place_and_no_staging()
    {
        Published(Folder, "old", fingerprint: "older");
        _generator.Fails(exitCode: 1, "Error: unsupported Daml-LF version 2.99");

        var result = await Update();

        var error = result.Messages.ShouldHaveSingleItem();
        (error.Severity, error.Code).ShouldBe((DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.CodegenFailed));
        error.Text.ShouldContain("unsupported Daml-LF version 2.99");
        result.Files.ShouldBeEmpty();
        Content().ShouldBe("old");
        WorkLeftovers().ShouldBeEmpty();
    }

    // A file a tool holds open keeps Windows from moving the folder it is in.
    [Fact, Trait("Category", "Unit")]
    public async Task A_previous_generation_that_cant_be_moved_aside_stays_and_the_build_fails()
    {
        Published(Folder, "old", fingerprint: "older");
        _generator.Writes(new() { ["Asset.cs"] = "new" });
        using (new FileStream(Path.Combine(Folder, "Asset.cs"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await Update();

            var error = result.Messages.Where(m => m.Severity == DamlDiagnosticSeverity.Error).ShouldHaveSingleItem();
            error.Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
            error.Text.ShouldContain("couldn't be moved aside");
            result.Files.ShouldBeEmpty();
        }
        Content().ShouldBe("old");
        WorkLeftovers().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_unfinished_publish_that_cant_be_recovered_is_DAML0109_and_nothing_is_generated()
    {
        var staging = KilledWithTheOldGenerationAside();
        using (new FileStream(Path.Combine(staging, "Asset.cs"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var result = await Update();

            var error = result.Messages.ShouldHaveSingleItem();
            (error.Severity, error.Code).ShouldBe((DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.RollbackFailed));
            error.Text.ShouldContain(FolderName);
            _generator.Calls.ShouldBeEmpty();
        }

        // Once nothing holds it, the next build puts the previous generation back.
        var next = await Update(Plan(GenerationStatus.Stale));
        next.Files.ShouldBe([new GeneratedFile(Path.Combine(Folder, "Asset.cs"), Id)]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Files_that_appeared_after_the_preflight_are_DAML0108_and_are_kept()
    {
        Published(Folder);
        File.WriteAllText(Path.Combine(Folder, "Mine.cs"), "class Mine { }");

        var result = await Update(Plan(GenerationStatus.Current));

        var error = result.Messages.ShouldHaveSingleItem();
        (error.Severity, error.Code).ShouldBe((DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.OutputConflict));
        error.Text.ShouldContain("Mine.cs");
        File.Exists(Path.Combine(Folder, "Mine.cs")).ShouldBeTrue();
        _generator.Calls.ShouldBeEmpty();
        result.Files.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Cancelling_the_generator_drops_its_staging_keeps_the_previous_generation_and_frees_the_root()
    {
        Published(Folder, "old", fingerprint: "older");
        _generator.IsCancelled();

        await Should.ThrowAsync<OperationCanceledException>(() => Update());

        Content().ShouldBe("old");
        WorkLeftovers().ShouldBeEmpty();
        using var free = await FileLock.AcquireAsync(OutputLock, TimeSpan.FromMilliseconds(300), CancellationToken.None);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Cancelling_while_waiting_for_the_root_touches_nothing()
    {
        Published(Folder, "old", fingerprint: "older");
        using var other = await FileLock.AcquireAsync(OutputLock, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => Update(cancellationToken: cancellation.Token));

        _generator.Calls.ShouldBeEmpty();
        Content().ShouldBe("old");
    }

    // Writes what a test gives it into the folder it is told to write to.
    private sealed class FakeGenerator : IProcessRunner
    {
        private readonly ConcurrentQueue<ProcessSpec> _calls = new();
        private Func<string, ProcessResult> _answer = _ => new ProcessResult(0, "", "");

        public IReadOnlyCollection<ProcessSpec> Calls => _calls;

        public void Writes(Dictionary<string, string> files) => _answer = output =>
        {
            foreach (var file in files)
            {
                DeepPath.Write(output, file.Key, file.Value);
            }
            return new ProcessResult(0, "", "");
        };

        public void Fails(int exitCode, string stderr) => _answer = _ => new ProcessResult(exitCode, "", stderr);

        public void IsCancelled() => _answer = _ => throw new OperationCanceledException();

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            _calls.Enqueue(spec);
            var arguments = spec.Arguments.ToList();
            return Task.FromResult(_answer(arguments[arguments.IndexOf("--out") + 1]));
        }

        public IRunningProcess Start(ProcessSpec spec) => throw new NotSupportedException();
    }
}
