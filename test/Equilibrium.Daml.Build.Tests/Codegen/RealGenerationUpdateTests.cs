using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Locking;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.Build.Tests.FileSystem;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Codegen;

// The whole update with the dpm and JDK installed on this machine and the real pinned generator.
// The generator's own repository ships a mini demo with the bindings it generates committed; built
// here, the demo must come out exactly as those.
public sealed class RealGenerationUpdateTests : IDisposable
{
    private const string FolderName = "Contracts-0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-real-update-" + Guid.NewGuid().ToString("N"));
    private readonly CountingRunner _generator = new(new ProcessRunner());

    // dpm build's package database has paths past 260 characters.
    public void Dispose() => DeepPath.Delete(_root);

    private static string MiniDemo => Path.Combine(Repository.Root, "third_party", "canton-dotnet-sdk-mini-demo");

    private static string Locks => LockNames.Root(Environment.GetEnvironmentVariable);

    private static string SharedCodegenRoot => GeneratorCache.DefaultRoot(Environment.GetEnvironmentVariable);

    private string Package => Path.Combine(_root, "daml");

    private string Root => Path.Combine(_root, @"App\obj\Debug\net10.0\Daml");

    private string Folder => Path.Combine(Root, FolderName);

    private string Work => Path.Combine(Root, GenerationPublisher.WorkFolderName);

    // A copy, so the build's .daml folder never lands in third_party.
    private async Task<string> BuildDarAsync(string dpm)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(MiniDemo, "daml"), "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(Package, file.Substring(Path.Combine(MiniDemo, "daml").Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        var build = await new ProcessRunner().RunAsync(
            new ProcessSpec(dpm, ["build"]) { WorkingDirectory = Package, Timeout = TimeSpan.FromMinutes(5) }, CancellationToken.None);
        build.ExitCode.ShouldBe(0, build.Stdout + build.Stderr);
        return Directory.GetFiles(Path.Combine(Package, @".daml\dist"), "*.dar").ShouldHaveSingleItem();
    }

    private CodegenRequest Request(string dpm, string java, string dar, bool offline, bool install) => new(
        Path.Combine(_root, @"App\App.csproj"),
        "Debug",
        "net10.0",
        new ToolOptions(dpm, offline, install, SkipPreflight: false, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5)),
        new CodegenOptions(true, ReleaseTools.CodegenComponent, GeneratedOutputMode.Intermediate, Root, JavaHome: Path.GetDirectoryName(Path.GetDirectoryName(java))),
        [new CodegenInput(
            new GenerationReference(DamlReferenceSource.DamlReference, @"..\Contracts\Contracts.msbuildproj",
                Path.Combine(_root, @"Contracts\Contracts.msbuildproj"), null, "0123456789abcdef", FolderName),
            dar)],
        DesignTime: false,
        RuntimeVersion: "0.6.0")
    { LedgerAbstractionsVersion = "0.6.0" };

    private static Task<CodegenPlan> PlanAsync(CodegenRequest request) =>
        new CodegenPreflight(new ProcessRunner(), new PhysicalFileSystem(), Environment.GetEnvironmentVariable, SharedCodegenRoot, Locks)
            .PlanAsync(request, CancellationToken.None);

    private Task<CodegenResult> UpdateAsync(CodegenRequest request, CodegenPlan plan, CancellationToken cancellationToken = default) =>
        new GenerationUpdate(new GeneratorRun(_generator, Environment.GetEnvironmentVariable), Locks).RunAsync(request, plan, cancellationToken);

    // Installs the generator once on a machine that hasn't got it yet; the build itself is then offline.
    private async Task<(CodegenRequest Request, CodegenPlan Plan)> PlannedOfflineAsync()
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        var dar = await BuildDarAsync(dpm);
        var preinstall = await PlanAsync(Request(dpm, java, dar, offline: false, install: true));
        preinstall.Problems.ShouldBeEmpty(Describe(preinstall.Problems));

        var request = Request(dpm, java, dar, offline: true, install: false);
        var plan = await PlanAsync(request);
        plan.Problems.ShouldBeEmpty(Describe(plan.Problems));
        return (request, plan);
    }

    private static string Describe(IEnumerable<ConfigurationError> problems) => string.Join("\n", problems.Select(p => $"{p.Code}: {p.Message}"));

    private static string Describe(IEnumerable<BuildMessage> messages) => string.Join("\n", messages.Select(m => $"{m.Code}: {m.Text}"));

    private static List<(string Path, string Sha256)> Tree(string folder, IEnumerable<string> files) => files
        .Select(f => (f.Substring(folder.Length + 1), Hashing.Sha256File(f)))
        .OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase)
        .ToList();

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task The_mini_demo_comes_out_as_the_committed_bindings_and_the_next_build_runs_nothing()
    {
        var (request, plan) = await PlannedOfflineAsync();

        var result = await UpdateAsync(request, plan);

        result.Messages.Where(m => m.Severity == DamlDiagnosticSeverity.Error).ShouldBeEmpty(Describe(result.Messages));
        var committed = Path.Combine(MiniDemo, @"src\MiniDemo.Contracts\Generated");
        Tree(Folder, result.Files.Select(f => f.Path)).ShouldBe(Tree(committed, Directory.GetFiles(committed, "*", SearchOption.AllDirectories)));
        _generator.Calls.ShouldBe(1);
        // The package id read from the DAR is the one the generator registers.
        var package = GenerationManifest.Read(Path.Combine(Folder, GenerationManifest.FileName))!.Package.ShouldNotBeNull();
        (package.PackageName, package.PackageVersion, package.SdkVersion).ShouldBe(("canton-mini-demo-hbde402d4f83a", "0.1.0", "3.5.2"));
        result.Files.ShouldContain(f => Path.GetFileName(f.Path) == $"PackageRegistration_{package.MainPackageId}.cs");

        var again = await PlanAsync(request);
        again.Generations.Single().State.Status.ShouldBe(GenerationStatus.Current);
        var second = await UpdateAsync(request, again);

        _generator.Calls.ShouldBe(1);
        second.Files.ShouldBe(result.Files);
    }

    // The job takes the generator's whole tree down, so nothing holds the staging or the temp folder.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public async Task A_build_cancelled_while_the_generator_runs_publishes_nothing_and_the_next_one_generates()
    {
        var (request, plan) = await PlannedOfflineAsync();
        using var cancellation = new CancellationTokenSource();
        _generator.Started = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Should.ThrowAsync<OperationCanceledException>(() => UpdateAsync(request, plan, cancellation.Token));

        Directory.Exists(Folder).ShouldBeFalse();
        _generator.Started = null;
        var result = await UpdateAsync(request, await PlanAsync(request));
        result.Messages.Where(m => m.Severity == DamlDiagnosticSeverity.Error).ShouldBeEmpty(Describe(result.Messages));
        result.Files.Count.ShouldBe(7);
        // With nothing left in it, the work folder goes too.
        Directory.Exists(Work).ShouldBeFalse();
    }

    private sealed class CountingRunner(IProcessRunner inner) : IProcessRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Action? Started { get; set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Started?.Invoke();
            return inner.RunAsync(spec, cancellationToken);
        }

        public IRunningProcess Start(ProcessSpec spec) => inner.Start(spec);
    }
}
