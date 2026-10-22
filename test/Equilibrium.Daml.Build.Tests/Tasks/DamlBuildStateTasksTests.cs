using System;
using System.IO;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Tasks;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tasks;

public sealed class DamlBuildStateTasksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-state-tasks-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBuildEngine _engine = new();

    public DamlBuildStateTasksTests()
    {
        Write("daml.yaml", "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n");
        Write(@"daml\Main.daml", "module Main where\n");
        Write(@"vendor\finance-1.0.0.dar", "finance");
        Write(@".daml\dist\contracts-1.2.0.dar", "dar");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string At(string relative) => Path.Combine(_root, relative);

    private void Write(string relative, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(At(relative))!);
        File.WriteAllText(At(relative), content);
    }

    private DamlComputeBuildState Compute(string? buildEnabled = null)
    {
        var dependency = new TaskItem("finance");
        dependency.SetMetadata("DarPath", At(@"vendor\finance-1.0.0.dar"));
        return new DamlComputeBuildState
        {
            BuildEngine = _engine,
            ManifestPath = At("daml.yaml"),
            SourcePath = At("daml"),
            DarPath = At(@".daml\dist\contracts-1.2.0.dar"),
            StatePath = At(@"obj\daml.build.state"),
            Dependencies = [dependency, new TaskItem("daml-stdlib")],
            BuildEnabled = buildEnabled,
        };
    }

    // What the build task records once dpm made the DAR.
    private static void Record(DamlComputeBuildState computed) =>
        new PackageBuildState(computed.Fingerprint, computed.SourceFingerprint).Record(computed.StatePath, computed.DarPath);

    [Fact, Trait("Category", "Unit")]
    public void A_package_never_built_is_built_and_once_recorded_isnt_built_again()
    {
        var first = Compute();
        first.Execute().ShouldBeTrue();
        first.ShouldBuild.ShouldBeTrue();
        Record(first);

        var second = Compute();
        second.Execute().ShouldBeTrue();

        second.ShouldBuild.ShouldBeFalse();
        second.SourceFingerprint.ShouldBe(first.SourceFingerprint);
        _engine.Messages.ShouldContain(m => m.Message!.Contains("is up to date"));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_changed_dependency_DAR_builds_again()
    {
        var first = Compute();
        first.Execute();
        Record(first);
        Write(@"vendor\finance-1.0.0.dar", "finance 1.0.1");

        var second = Compute();
        second.Execute().ShouldBeTrue();

        second.ShouldBuild.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public void Switched_off_nothing_is_built_and_a_value_it_cant_use_is_DAML0100()
    {
        var off = Compute("no");
        off.Execute().ShouldBeTrue();
        off.ShouldBuild.ShouldBeFalse();

        Compute("sometimes").Execute().ShouldBeFalse();
        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
    }
}
