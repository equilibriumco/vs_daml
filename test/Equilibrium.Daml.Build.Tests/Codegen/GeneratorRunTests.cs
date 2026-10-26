using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.Build.Tests.FileSystem;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Codegen;

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
        TestDar.Write(Dar);
        _publisher = new GenerationPublisher(Path.Combine(_root, @"App\obj\Debug\net10.0\Daml"));
    }

    public void Dispose() => DeepPath.Delete(_root);

    private const string Dpm = @"C:\dpm\bin\dpm.exe";
    private const string Java = @"C:\jdk-21\bin\java.exe";
    private const string Project = @"C:\work\App\App.csproj";

    private string Dar => Path.Combine(_root, @"Contracts\.daml\dist\contracts-1.2.0.dar");

    private static readonly SourceProvenance Sources = new(@"C:\work\Contracts\daml.yaml", "50c");

    private CodegenTools Tools => new(Dpm, Java, "1.0.22", new GeneratorCache(_generator.Root, _generator.Pin));

    // Where the generation is published once the run is over; its manifest's paths are relative to it.
    private const string Published = @"C:\work\App\obj\Daml\Contracts-0123456789abcdef";

    private PlannedGeneration Planned(string? @namespace = "Contracts")
    {
        var reference = new GenerationReference(DamlReferenceSource.EqDamlReference, @"..\Contracts\Contracts.msbuildproj",
            @"C:\work\Contracts\Contracts.msbuildproj", @namespace, "0123456789abcdef", "Contracts-0123456789abcdef");
        return new PlannedGeneration(new CodegenInput(reference, Dar, Sources), Published, "f1n9", new GenerationState(GenerationStatus.Stale, "", [], []));
    }

    // The copy of the DAR the generator is given, in the run folder.
    private static string Snapshot(string staging) => GeneratorRun.DarSnapshotOf(staging);

    private static string[] Arguments(string dar, string staging, string? @namespace) =>
        @namespace is null ? ["codegen-cs", "--dar", dar, "--out", staging] : ["codegen-cs", "--dar", dar, "--out", staging, "--", "-n", @namespace];

    // The generator writing its files where it was told.
    private void Generates(string staging, IReadOnlyDictionary<string, string> files, string? @namespace = "Contracts", int exitCode = 0, string stderr = "") =>
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, @namespace), spec =>
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

    private Task<Equilibrium.Daml.Build.Core.Configuration.ConfigurationError?> Run(string staging, PlannedGeneration? planned = null, TimeSpan? timeout = null) =>
        new GeneratorRun(_runner, name => name == "PATH" ? @"C:\Windows\system32;C:\tools" : null)
            .RunAsync(Tools, planned ?? Planned(), staging, Project, timeout ?? TimeSpan.FromMinutes(5), CancellationToken.None);

    private static readonly Dictionary<string, string> AssetFiles = new()
    {
        [@"Contracts\Kinds\Asset\Asset.cs"] = "public sealed partial record Asset",
        [@"Contracts\Kinds\Asset\Asset.Give.cs"] = "partial record Asset",
    };

    // A parallel build may rebuild the DAR while the generator runs. The C#, the package the
    // manifest names and the hash it records must all come from the one copy the generator was given.
    [Fact, Trait("Category", "Unit")]
    public async Task The_generator_and_the_manifest_see_one_copy_of_the_DAR()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        var given = Hashing.Sha256File(Dar);
        string? generatorSaw = null;
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, "Contracts"), spec =>
        {
            generatorSaw = Hashing.Sha256File(spec.Arguments[2]);
            File.Delete(Dar);
            TestDar.Write(Dar, "contracts-1.2.1");
            File.WriteAllText(Path.Combine(staging, "Asset.cs"), "partial record Asset");
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        (await Run(staging)).ShouldBeNull();

        var manifest = GenerationManifest.Read(Path.Combine(staging, GenerationManifest.FileName), Published).ShouldNotBeNull();
        generatorSaw.ShouldBe(given);
        manifest.DarSha256.ShouldBe(given);
        manifest.Package.ShouldNotBeNull().PackageVersion.ShouldBe("1.2.0");
    }

    // The fingerprint says which DAR the code came from; a DAR other than the one the plan hashed
    // gets a fingerprint of its own, or the next build would take the code for current.
    [Fact, Trait("Category", "Unit")]
    public async Task A_DAR_other_than_the_planned_one_gets_its_own_fingerprint()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        Generates(staging, AssetFiles);
        var planned = Planned() with { DarSha256 = "the DAR as the preflight saw it" };

        (await Run(staging, planned)).ShouldBeNull();

        var manifest = GenerationManifest.Read(Path.Combine(staging, GenerationManifest.FileName), Published).ShouldNotBeNull();
        manifest.Fingerprint.ShouldBe(GenerationFingerprint.Of(planned.Input, Hashing.Sha256File(Dar), Tools.Cache.Pin.Digest, Tools.DpmVersion));
    }

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

        // The run writes the manifest for where the generation will be published, not for staging.
        File.ReadAllText(Path.Combine(staging, GenerationManifest.FileName)).ShouldContain(@"owner=..\..\..\App.csproj");
        var manifest = GenerationManifest.Read(Path.Combine(staging, GenerationManifest.FileName), Published).ShouldNotBeNull();
        (manifest.GenerationId, manifest.Fingerprint, manifest.Owner, manifest.Namespace, manifest.Dar).ShouldBe(("0123456789abcdef", "f1n9", Project, "Contracts", Dar));
        (manifest.DarSha256, manifest.Generator).ShouldBe((Hashing.Sha256File(Dar), _generator.Pin.Reference));
        manifest.Package.ShouldBe(new DarProvenance("contracts", "1.2.0", "3.6.0", TestDar.MainPackageId, [TestDar.DependencyPackageId]));
        manifest.Source.ShouldBe(Sources);
        manifest.Files.ShouldBe(
        [
            new GenerationManifestFile(@"Contracts\Kinds\Asset\Asset.cs", Hashing.Sha256Text("public sealed partial record Asset")),
            new GenerationManifestFile(@"Contracts\Kinds\Asset\Asset.Give.cs", Hashing.Sha256Text("partial record Asset")),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_daml_yaml_of_the_run_names_only_the_pinned_component()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        string? manifest = null;
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, "Contracts"), spec =>
        {
            manifest = File.ReadAllText(Path.Combine(spec.WorkingDirectory!, "daml.yaml"));
            File.WriteAllText(Path.Combine(staging, "A.cs"), "class A { }");
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        (await Run(staging)).ShouldBeNull();

        manifest.ShouldBe($"name: daml-codegen\nversion: 0.0.1\ncomponents:\n  - {_generator.Pin.Reference}\n");
    }

    // dpm looks for a multi-package.yaml above its working folder, and one with an sdk-version (as
    // dpm new makes it) clashes with the pinned component; one in the run folder ends the search.
    [Fact, Trait("Category", "Unit")]
    public async Task The_run_keeps_dpm_from_a_multi_package_yaml_above_it()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        string? multiPackage = null;
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, "Contracts"), spec =>
        {
            multiPackage = File.ReadAllText(Path.Combine(spec.WorkingDirectory!, "multi-package.yaml"));
            File.WriteAllText(Path.Combine(staging, "A.cs"), "class A { }");
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        (await Run(staging)).ShouldBeNull();

        multiPackage.ShouldBe("packages: []\n");
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
    public async Task A_failing_generator_is_EQD0104_with_its_own_words_and_no_secrets()
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

    // Said before the generator starts, rather than in its own words after it fails.
    [Fact, Trait("Category", "Unit")]
    public async Task A_DAR_that_isnt_one_is_EQD0104_and_nothing_runs()
    {
        File.WriteAllText(Dar, "not a DAR");

        var problem = (await Run(_publisher.NewStaging("Contracts-0123456789abcdef"))).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
        problem.Message.ShouldContain($"{Dar} isn't a DAR");
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_generator_that_writes_no_csharp_is_EQD0104()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        Generates(staging, new Dictionary<string, string> { ["notes.txt"] = "nothing" });

        var problem = (await Run(staging)).ShouldNotBeNull();

        (problem.Code, problem.Message).ShouldBe((DamlDiagnosticCodes.CodegenFailed,
            $@"dpm codegen-cs wrote no C# for ..\Contracts\Contracts.msbuildproj from {Dar}."));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_generator_past_the_time_limit_is_EQD0110()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, "Contracts"), new TimeoutException("ran past its limit"));

        var problem = (await Run(staging, timeout: TimeSpan.FromSeconds(90))).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.CancelledOrTimedOut);
        problem.Message.ShouldContain("90 s");
        problem.Message.ShouldContain("EqDamlProcessTimeoutSeconds");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_file_past_the_path_limit_goes_into_the_manifest()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, "Contracts"), _ =>
        {
            DeepPath.Write(staging, DeepPath.Relative, "class Registration { }");
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        (await Run(staging)).ShouldBeNull();

        GenerationManifest.Read(Path.Combine(staging, GenerationManifest.FileName), Published)!.Files
            .ShouldBe([new GenerationManifestFile(DeepPath.Relative, Hashing.Sha256Text("class Registration { }"))]);
    }

    // A file the generator wrote but something holds can't be hashed; that is a diagnostic, not a crash.
    [Fact, Trait("Category", "Unit")]
    public async Task Output_that_cant_be_read_back_is_EQD0104()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        FileStream? held = null;
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, "Contracts"), _ =>
        {
            held = new FileStream(Path.Combine(staging, "Asset.cs"), FileMode.Create, FileAccess.Write, FileShare.None);
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        var problem = (await Run(staging)).ShouldNotBeNull();
        Release(held!);

        problem.Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
        problem.Message.ShouldContain("couldn't be read back");
    }

    private static void Release(FileStream file) => file.Dispose();

    // A file still held in the run folder mustn't fail a generation that worked.
    [Fact, Trait("Category", "Unit")]
    public async Task A_run_folder_that_wont_go_is_left_for_the_next_recovery()
    {
        var staging = _publisher.NewStaging("Contracts-0123456789abcdef");
        FileStream? held = null;
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, "Contracts"), spec =>
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
        _runner.On(Dpm, Arguments(Snapshot(staging), staging, "Contracts"), spec =>
        {
            _runFolder = spec.WorkingDirectory;
            return Task.FromException<ProcessResult>(new OperationCanceledException());
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Run(staging));

        Directory.Exists(_runFolder).ShouldBeFalse();
    }
}
