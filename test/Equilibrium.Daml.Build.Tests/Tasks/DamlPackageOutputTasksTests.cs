using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.Locking;
using Equilibrium.Daml.Build.Tasks;
using Equilibrium.Daml.Build.Tests.Codegen;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tasks;

public sealed class DamlPackageOutputTasksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-package-tasks-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBuildEngine _engine = new();

    public DamlPackageOutputTasksTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "daml"));
        File.WriteAllText(Manifest, "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n");
        File.WriteAllText(Path.Combine(_root, @"daml\Main.daml"), "module Main where\n");
        TestDar.Write(Dar);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Manifest => Path.Combine(_root, "daml.yaml");

    private string Dar => Path.Combine(_root, @".daml\dist\contracts-1.2.0.dar");

    private string State => Path.Combine(_root, @"obj\daml.build.state");

    private PackageBuildState Built()
    {
        var state = PackageBuildState.Compute(Manifest, Path.Combine(_root, "daml"), []);
        state.Record(State, Dar);
        return state;
    }

    private DamlReadDarProvenance Provenance() => new() { BuildEngine = _engine, DarPath = Dar, StatePath = State };

    [Fact, Trait("Category", "Unit")]
    public void A_built_DAR_names_its_package_id_and_the_sources_it_was_built_from()
    {
        // This task needs only the archive manifest; code generation validates the payload separately.
        using (var zip = ZipFile.Open(Dar, ZipArchiveMode.Update))
        using (var payload = new StreamWriter(zip.CreateEntry($"contracts-1.2.0-{TestDar.MainPackageId}/contracts-1.2.0-{TestDar.MainPackageId}.dalf").Open()))
        {
            payload.Write("not a valid DALF");
        }
        var built = Built();
        var task = Provenance();

        task.Execute().ShouldBeTrue();

        (task.MainPackageId, task.SourceFingerprint).ShouldBe((TestDar.MainPackageId, built.SourceFingerprint));
    }

    // Only reads, so a referencing project's design-time build can ask before any real build.
    [Fact, Trait("Category", "Unit")]
    public void Without_a_DAR_or_a_recorded_build_the_answers_are_empty_and_nothing_fails()
    {
        var task = Provenance();
        task.Execute().ShouldBeTrue();
        (task.MainPackageId, task.SourceFingerprint).ShouldBe((TestDar.MainPackageId, ""));

        File.WriteAllText(Dar, "not a DAR");
        var unreadable = Provenance();
        unreadable.Execute().ShouldBeTrue();
        unreadable.MainPackageId.ShouldBe("");

        File.Delete(Dar);
        var missing = Provenance();
        missing.Execute().ShouldBeTrue();
        (missing.MainPackageId, missing.SourceFingerprint).ShouldBe(("", ""));
        _engine.Errors.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Clean_removes_the_DAR_and_the_record_and_leaves_the_rest_of_the_package()
    {
        Built();
        File.WriteAllText(Path.Combine(_root, @".daml\dist\other.txt"), "kept");

        new DamlCleanPackage { BuildEngine = _engine, ManifestPath = Manifest, DarPath = Dar, StatePath = State }.Execute().ShouldBeTrue();

        (File.Exists(Dar), File.Exists(State)).ShouldBe((false, false));
        File.Exists(Path.Combine(_root, @".daml\dist\other.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, @"daml\Main.daml")).ShouldBeTrue();
    }

    // Every configuration builds the package in its one .daml folder; clean waits for them like they wait for each other.
    [Fact, Trait("Category", "Unit")]
    public void Clean_of_a_package_another_build_holds_too_long_is_EQD0105_and_removes_nothing()
    {
        Built();
        using var held = FileLock.Acquire(LockNames.Package(LockNames.Root(Environment.GetEnvironmentVariable), _root), TimeSpan.FromSeconds(5), CancellationToken.None);

        new DamlCleanPackage { BuildEngine = _engine, ManifestPath = Manifest, DarPath = Dar, StatePath = State, LockTimeoutSeconds = "1" }.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.LockTimeout);
        File.Exists(Dar).ShouldBeTrue();
    }
}
