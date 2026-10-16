using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public sealed class GeneratorRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-generator-run-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private readonly SyntheticGeneratorCache _generator;
    private readonly GenerationPublisher _publisher;
    private string? _runFolder;

    public GeneratorRunTests()
    {
        _generator = SyntheticGeneratorCache.Create(Path.Combine(_root, "codegen"));
        Directory.CreateDirectory(Path.GetDirectoryName(Dar)!);
        File.WriteAllText(Dar, "a DAR");
        _publisher = new GenerationPublisher(Path.Combine(_root, @"App\obj\Debug\net10.0\Daml"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string Dpm = @"C:\dpm\bin\dpm.exe";
    private const string Java = @"C:\jdk-21\bin\java.exe";
    private const string Project = @"C:\work\App\App.csproj";

    private string Dar => Path.Combine(_root, @"Contracts\.daml\dist\contracts-1.2.0.dar");

    private CodegenTools Tools => new(Dpm, Java, "1.0.22", new GeneratorCache(_generator.Root, _generator.Pin));

    private PlannedGeneration Planned(string? @namespace = "Contracts")
    {
        var reference = new GenerationReference(DamlReferenceSource.DamlReference, @"..\Contracts\Contracts.msbuildproj",
            @"C:\work\Contracts\Contracts.msbuildproj", @namespace, "0123456789abcdef", "Contracts-0123456789abcdef");
        return new PlannedGeneration(new CodegenInput(reference, Dar), @"C:\unused", "f1n9", new GenerationState(GenerationStatus.Stale, "", [], []));
    }

    private static string[] Arguments(string dar, string staging, string? @namespace) =>
        @namespace is null ? ["codegen-cs", "--dar", dar, "--out", staging] : ["codegen-cs", "--dar", dar, "--out", staging, "--", "-n", @namespace];

    // The generator writing its files where it was told.
    private void Generates(string staging, IReadOnlyDictionary<string, string> files, string? @namespace = "Contracts", int exitCode = 0, string stderr = "") =>
        _runner.On(Dpm, Arguments(Dar, staging, @namespace), spec =>
        {
            _runFolder = spec.WorkingDirectory;
            foreach (var file in files)
            {
                var path = Path.Combine(staging, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Value);
            }
            return Task.FromResult(new ProcessResult(exitCode, "", stderr));
        });

    private Task<Daml.Build.Core.Configuration.ConfigurationError?> Run(string staging, PlannedGeneration? planned = null, TimeSpan? timeout = null) =>
        new GeneratorRun(_runner, name => name == "PATH" ? @"C:\Windows\system32;C:\tools" : null)
            .RunAsync(Tools, planned ?? Planned(), staging, Project, timeout ?? TimeSpan.FromMinutes(5), CancellationToken.None);

    private static readonly Dictionary<string, string> AssetFiles = new()
    {
        [@"Contracts\S09\Asset\Asset.cs"] = "public sealed partial record Asset",
        [@"Contracts\S09\Asset\Asset.Give.cs"] = "partial record Asset",
    };

    [Fact, Trait("Category", "Unit")]
    public async Task The_generator_runs_on_the_pinned_home_and_the_projects_java_and_its_files_get_a_manifest()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        Generates(staging, AssetFiles);

        (await Run(staging)).ShouldBeNull();

        var call = _runner.Calls.ShouldHaveSingleItem();
        File.Exists(Path.Combine(call.WorkingDirectory!, "daml.yaml")).ShouldBeFalse("the run folder is removed afterwards");
        call.Environment["DPM_HOME"].ShouldBe(_generator.Home);
        call.Environment["DPM_AUTO_INSTALL"].ShouldBe("false");
        call.Environment["PATH"].ShouldBe(@"C:\jdk-21\bin;C:\Windows\system32;C:\tools");
        call.Environment["JAVA_HOME"].ShouldBe(@"C:\jdk-21");
        call.Environment["TEMP"].ShouldStartWith(call.WorkingDirectory!);
        call.Environment["TMP"].ShouldBe(call.Environment["TEMP"]);
        Directory.Exists(_runFolder).ShouldBeFalse();

        var manifest = GenerationManifest.Read(Path.Combine(staging, GenerationManifest.FileName)).ShouldNotBeNull();
        (manifest.GenerationId, manifest.Fingerprint, manifest.Owner, manifest.Namespace, manifest.Dar).ShouldBe(("0123456789abcdef", "f1n9", Project, "Contracts", Dar));
        (manifest.DarSha256, manifest.Generator).ShouldBe((Hashing.Sha256File(Dar), _generator.Pin.Reference));
        manifest.Files.ShouldBe(
        [
            new GenerationManifestFile(@"Contracts\S09\Asset\Asset.cs", Hashing.Sha256Text("public sealed partial record Asset")),
            new GenerationManifestFile(@"Contracts\S09\Asset\Asset.Give.cs", Hashing.Sha256Text("partial record Asset")),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_daml_yaml_of_the_run_names_only_the_pinned_component()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        string? manifest = null;
        _runner.On(Dpm, Arguments(Dar, staging, "Contracts"), spec =>
        {
            manifest = File.ReadAllText(Path.Combine(spec.WorkingDirectory!, "daml.yaml"));
            File.WriteAllText(Path.Combine(staging, "A.cs"), "class A { }");
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        (await Run(staging)).ShouldBeNull();

        manifest.ShouldBe($"name: daml-codegen\nversion: 0.0.1\ncomponents:\n  - {_generator.Pin.Reference}\n");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_a_namespace_nothing_is_passed_to_the_emitter()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        Generates(staging, AssetFiles, @namespace: null);

        (await Run(staging, Planned(@namespace: null))).ShouldBeNull();

        _runner.Calls.Single().Arguments.ShouldNotContain("--");
    }

    // JDK 26 and SLF4J print warnings even on success; only the real lines explain a failure.
    [Fact, Trait("Category", "Unit")]
    public async Task A_failing_generator_is_DAML0104_with_its_own_words_and_no_secrets()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        Generates(staging, new Dictionary<string, string>(), exitCode: 1, stderr:
            "WARNING: A terminally deprecated method in sun.misc.Unsafe has been called\n" +
            "SLF4J: No SLF4J providers were found.\n" +
            "dpm-codegen-cs: failed to read https://ann:s3cret@mirror.example/dar\n" +
            "Error: unsupported Daml-LF version 2.99\n");

        var problem = (await Run(staging)).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
        problem.Message.ShouldBe(
            @"dpm codegen-cs failed for ..\Contracts\Contracts.msbuildproj (exit code 1):" + "\n" +
            "dpm-codegen-cs: failed to read https://***@mirror.example/dar\nError: unsupported Daml-LF version 2.99");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_generator_that_writes_no_csharp_is_DAML0104()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        Generates(staging, new Dictionary<string, string> { ["notes.txt"] = "nothing" });

        var problem = (await Run(staging)).ShouldNotBeNull();

        (problem.Code, problem.Message).ShouldBe((DamlDiagnosticCodes.CodegenFailed,
            $@"dpm codegen-cs wrote no C# for ..\Contracts\Contracts.msbuildproj from {Dar}."));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_generator_past_the_time_limit_is_DAML0110()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        _runner.On(Dpm, Arguments(Dar, staging, "Contracts"), new TimeoutException("ran past its limit"));

        var problem = (await Run(staging, timeout: TimeSpan.FromSeconds(90))).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.CancelledOrTimedOut);
        problem.Message.ShouldContain("90 s");
        problem.Message.ShouldContain("DamlProcessTimeoutSeconds");
    }

    private static void Release(FileStream file) => file.Dispose();

    // A file still held in the run folder mustn't fail a generation that worked.
    [Fact, Trait("Category", "Unit")]
    public async Task A_run_folder_that_wont_go_is_left_for_the_next_recovery()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        FileStream? held = null;
        _runner.On(Dpm, Arguments(Dar, staging, "Contracts"), spec =>
        {
            _runFolder = spec.WorkingDirectory;
            File.WriteAllText(Path.Combine(staging, "A.cs"), "class A { }");
            held = new FileStream(Path.Combine(spec.Environment["TEMP"]!, "hsperfdata"), FileMode.Create, FileAccess.Write, FileShare.None);
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        (await Run(staging)).ShouldBeNull();
        Release(held!);

        Directory.Exists(_runFolder).ShouldBeTrue();
        _publisher.Publish("Contracts-0123456789abcdef", staging);
        _publisher.Recover("Contracts-0123456789abcdef").ShouldBe(RecoveryOutcome.Cleaned);
        Directory.Exists(_runFolder).ShouldBeFalse();
        File.Exists(Path.Combine(_root, @"App\obj\Debug\net10.0\Daml\Contracts-0123456789abcdef\A.cs")).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Cancelling_stops_the_run_and_leaves_no_run_folder()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        _runner.On(Dpm, Arguments(Dar, staging, "Contracts"), spec =>
        {
            _runFolder = spec.WorkingDirectory;
            return Task.FromException<ProcessResult>(new OperationCanceledException());
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Run(staging));

        Directory.Exists(_runFolder).ShouldBeFalse();
    }
}
