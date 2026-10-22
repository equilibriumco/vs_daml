using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.Locking;
using Equilibrium.Daml.Build.Tasks;
using Equilibrium.Daml.TestHelpers;
using Microsoft.Build.Framework;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tasks;

public sealed class DpmToolTaskTests : IDisposable
{
    private readonly FakeDpm _dpm = FakeDpm.Create(new FakeDpmScenario());
    private readonly FakeBuildEngine _engine = new();

    public DpmToolTaskTests()
    {
        Directory.CreateDirectory(Package);
        File.WriteAllText(Manifest, "name: contracts\n");
    }

    private string Package => Path.Combine(_dpm.RootDirectory, "contracts");

    private string Manifest => Path.Combine(Package, "daml.yaml");

    private string Dar => Path.Combine(Package, @".daml\dist\contracts-1.2.0.dar");

    public void Dispose() => _dpm.Dispose();

    private T Prepare<T>(T task, params FakeDpmCommand[] commands)
        where T : DpmToolTask
    {
        var scenario = new FakeDpmScenario();
        scenario.Commands.AddRange(commands);
        scenario.Commands.Add(Version("version: 1.0.22\nbuild: b65a815\nbuildDate: b65a815\n"));
        _dpm.SetScenario(scenario);
        task.BuildEngine = _engine;
        task.ManifestPath = Manifest;
        task.DpmPath = _dpm.ExecutablePath;
        task.EnvironmentVariables = _dpm.Environment.Select(v => $"{v.Key}={v.Value}").ToArray();
        return task;
    }

    private DamlBuildTask Build(params FakeDpmCommand[] commands) => Prepare(new DamlBuildTask { DarPath = Dar }, commands);

    [Fact, Trait("Category", "Process")]
    public void Commands_receive_the_private_compiler_cache_instead_of_an_inherited_override()
    {
        var command = CreatesTheDar();
        var first = Build(command);
        var inherited = Path.Combine(Package, "inherited-cache");
        first.EnvironmentVariables = first.EnvironmentVariables.Concat(["DAML_CACHE=" + inherited]).ToArray();
        first.Execute().ShouldBeTrue();
        Build(command).Execute().ShouldBeTrue();
        var caches = _dpm.ReadCalls().Where(c => c.Args.SequenceEqual(new[] { "build" }))
            .Select(c => c.Environment["DAML_CACHE"]).ToArray();
        caches.Length.ShouldBe(2);
        caches.ShouldAllBe(p => Path.IsPathRooted(p) && p != inherited);
    }

    [Fact, Trait("Category", "Process")]
    public void Tool_credentials_are_redacted_from_detailed_logs_and_failure_errors()
    {
        var command = Command("build", "fetch oci://alice:secret@registry/contracts\n");
        command.ExitCode = 1;
        Build(command).Execute().ShouldBeFalse();
        _engine.Messages.ShouldContain(m => m.Message!.Contains("oci://***@registry/contracts"));
        _engine.Messages.ShouldAllBe(m => !m.Message!.Contains("secret"));
        _engine.Errors.ShouldNotBeEmpty();
        _engine.Errors.ShouldAllBe(e => !e.Message!.Contains("secret"));
    }

    private string Source => Path.Combine(Package, "daml");

    private string StatePath => Path.Combine(Package, @"obj\daml.build.state");

    // A package with sources, the state DamlComputeBuildState found it in before the build, and a
    // build task that knows it.
    private (DamlBuildTask Task, PackageBuildState State) BuildFromState(params FakeDpmCommand[] commands)
    {
        File.WriteAllText(Manifest, "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n");
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "Main.daml"), "module Main where\n");
        var state = PackageBuildState.Compute(Manifest, Source, [], Environment.GetEnvironmentVariable, _dpm.ExecutablePath);
        var task = Build(commands);
        task.SourcePath = Source;
        task.StatePath = StatePath;
        task.Fingerprint = state.Fingerprint;
        task.SourceFingerprint = state.SourceFingerprint;
        return (task, state);
    }

    private static FakeDpmCommand CreatesTheDar()
    {
        var command = Command("build", "Compiling contracts to a DAR.\nCreated .daml\\dist\\contracts-1.2.0.dar\n");
        command.WriteFiles[@".daml\dist\contracts-1.2.0.dar"] = "dar";
        return command;
    }

    // Two builds of one package (Debug and Release, or Visual Studio's and a project's that needs the
    // DAR) both found it out of date; the one that waits for the other finds the DAR made from the
    // same inputs and leaves it alone rather than rewrite it under whoever reads it.
    [Fact, Trait("Category", "Process")]
    public void A_DAR_another_build_made_while_this_one_waited_isnt_built_again()
    {
        var (task, state) = BuildFromState(CreatesTheDar());
        Directory.CreateDirectory(Path.GetDirectoryName(Dar)!);
        File.WriteAllText(Dar, "dar");
        state.Record(StatePath, Dar);

        task.Execute().ShouldBeTrue();

        ToolCalls().ShouldBeEmpty();
        task.CreatedDar.ShouldBeNull();
        _engine.Messages.ShouldContain(m => m.Message!.Contains("another build"));
    }

    [Fact, Trait("Category", "Process")]
    public void A_record_from_before_an_edit_does_not_skip_a_build_that_was_waiting()
    {
        var (task, state) = BuildFromState(CreatesTheDar());
        Directory.CreateDirectory(Path.GetDirectoryName(Dar)!);
        File.WriteAllText(Dar, "old dar");
        state.Record(StatePath, Dar);
        File.AppendAllText(Path.Combine(Source, "Main.daml"), "x = 1\n");

        task.Execute().ShouldBeTrue();

        ToolCalls().ShouldHaveSingleItem();
        File.ReadAllText(Dar).ShouldBe("dar");
    }

    // While it still holds the package, so another build waiting for it finds the record.
    [Fact, Trait("Category", "Process")]
    public void The_build_records_what_its_DAR_was_built_from()
    {
        var (task, state) = BuildFromState(CreatesTheDar());

        task.Execute().ShouldBeTrue();

        task.CreatedDar.ShouldBe(Dar);
        state.IsRecordedIn(StatePath, Dar).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Process")]
    public void Sources_changed_while_dpm_built_are_not_recorded_as_built()
    {
        var build = CreatesTheDar();
        build.WriteFiles[@"daml\Main.daml"] = "module Main where\nx = 1\n";
        var (task, _) = BuildFromState(build);

        task.Execute().ShouldBeTrue();

        File.Exists(StatePath).ShouldBeFalse();
        _engine.Messages.ShouldContain(m => m.Message!.Contains("changed while dpm built it"));
    }

    // Only the next build pays for a record that couldn't be written: it builds again.
    [Fact, Trait("Category", "Process")]
    public void A_record_that_cant_be_written_is_a_warning()
    {
        var (task, _) = BuildFromState(CreatesTheDar());
        Directory.CreateDirectory(StatePath);

        task.Execute().ShouldBeTrue();

        _engine.Warnings.ShouldHaveSingleItem().Message!.ShouldContain("builds it again");
    }

    private static FakeDpmCommand Version(string stdout)
    {
        var command = new FakeDpmCommand { Stdout = stdout };
        command.Args.Add("--version");
        return command;
    }

    private static bool IsVersionProbe(FakeDpmCall call) => call.Args.Count == 1 && call.Args[0] == "--version";

    // The calls of the command under test, without the version check before it.
    private System.Collections.Generic.List<FakeDpmCall> ToolCalls() => _dpm.ReadCalls().Where(c => !IsVersionProbe(c)).ToList();

    private int VersionProbes() => _dpm.ReadCalls().Count(IsVersionProbe);

    private static FakeDpmCommand Command(string verb, string stderr, int exitCode = 0, int delay = 0)
    {
        var command = new FakeDpmCommand { Stderr = stderr, ExitCode = exitCode, DelayMilliseconds = delay };
        command.Args.Add(verb);
        return command;
    }

    private static string Captured(string name) =>
        File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "build-output", name));

    // dpm builds the package in place; two builds of it at once would write one .daml folder.
    [Fact, Trait("Category", "Process")]
    public void Dpm_waits_for_another_build_of_the_package_and_gives_up_with_DAML0105()
    {
        var task = Build(Command("build", "Created .daml\\dist\\contracts-1.2.0.dar\n"));
        task.LockTimeoutSeconds = "1";
        using var held = FileLock.Acquire(
            LockNames.Package(LockNames.Root(Environment.GetEnvironmentVariable), Package),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        task.Execute().ShouldBeFalse();

        var error = _engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.LockTimeout);
        error.Message!.ShouldContain(Package);
        ToolCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Process")]
    public void A_build_runs_dpm_build_in_the_package_and_hands_on_the_DAR()
    {
        var command = Command("build", "Compiling contracts to a DAR.\nCreated .daml\\dist\\contracts-1.2.0.dar\n");
        command.WriteFiles[@".daml\dist\contracts-1.2.0.dar"] = "dar";
        var task = Build(command);

        task.Execute().ShouldBeTrue();

        task.CreatedDar.ShouldBe(Dar);
        (_engine.Errors.Count, _engine.Warnings.Count).ShouldBe((0, 0));
        var call = ToolCalls().ShouldHaveSingleItem();
        call.Args.ShouldBe(["build"]);
        call.WorkingDirectory.ShouldBe(Package);
        call.Environment["DPM_AUTO_INSTALL"].ShouldBe("false");
    }

    [Fact, Trait("Category", "Process")]
    public void Compile_errors_reach_MSBuild_at_their_place_in_UTF_8_and_nothing_repeats_them()
    {
        var task = Build(Command("build", Captured("build-broken.stderr.txt"), exitCode: 1));

        task.Execute().ShouldBeFalse();

        _engine.Errors.Select(e => (e.Code, e.Subcategory, e.File, e.LineNumber, e.ColumnNumber, e.EndLineNumber, e.EndColumnNumber)).ShouldBe(
        [
            ("EQD0001", "parser", Path.Combine(Package, @"daml\SyntaxError.daml"), 6, 1, 6, 1),
            ("EQD0001", "typecheck", Path.Combine(Package, @"daml\TypeError.daml"), 5, 9, 5, 20),
            ("EQD0001", "not found", Path.Combine(Package, @"daml\UnknownImport.daml"), 4, 8, 4, 23),
        ]);
        _engine.Errors[1].Message!.ShouldContain("Couldn't match expected type ‘Int’ with actual type ‘Text’");
        task.CreatedDar.ShouldBeNull();
    }

    [Fact, Trait("Category", "Process")]
    public void A_DAR_dpm_put_somewhere_else_fails_the_build()
    {
        var task = Build(Command("build", "Created out\\other.dar\n"));

        task.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        task.CreatedDar.ShouldBeNull();
    }

    [Fact, Trait("Category", "Process")]
    public void Offline_dpm_is_told_not_to_install_and_a_missing_SDK_is_DAML0107()
    {
        var task = Build(Command("build", "SDK_NOT_INSTALLED: target dpm-sdk version not installed. You can install the needed sdk via 'dpm install 3.4.0'\n", exitCode: 1));
        task.Offline = "true";

        task.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.MissingToolOffline);
        ToolCalls().ShouldHaveSingleItem().Environment["DPM_AUTO_INSTALL"].ShouldBe("false");
    }

    // The user's own DPM_AUTO_INSTALL must not override what the build allows.
    [Fact, Trait("Category", "Process")]
    public void Installing_is_up_to_the_build_whatever_the_environment_says()
    {
        var command = Command("build", "Created .daml\\dist\\contracts-1.2.0.dar\n");
        var task = Build(command);
        task.AllowToolInstall = "true";
        task.EnvironmentVariables = [.. task.EnvironmentVariables, "DPM_AUTO_INSTALL=false"];

        task.Execute().ShouldBeTrue();

        ToolCalls().ShouldHaveSingleItem().Environment["DPM_AUTO_INSTALL"].ShouldBe("true");
    }

    // dpm.cmd starting dpm.exe, like the real one: the limit has to stop both.
    [Fact, Trait("Category", "Process")]
    public void A_dpm_that_runs_too_long_is_stopped_with_everything_it_started()
    {
        var wrapper = Path.Combine(_dpm.BinDirectory, "dpm-wrapper.cmd");
        File.WriteAllText(wrapper, "@\"%~dp0dpm.exe\" %*\r\n");
        var task = Build(Command("build", "Compiling contracts to a DAR.\n", delay: 60_000));
        task.DpmPath = wrapper;
        task.ProcessTimeoutSeconds = "1";
        var clock = Stopwatch.StartNew();

        task.Execute().ShouldBeFalse();

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        var error = _engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.CancelledOrTimedOut);
        error.Message!.ShouldContain("EqDamlProcessTimeoutSeconds");
        WaitUntilNoFakeDpmRuns();
    }

    [Fact, Trait("Category", "Process")]
    public async Task Cancelling_stops_dpm_and_says_so()
    {
        var task = Build(Command("build", "Compiling contracts to a DAR.\n", delay: 60_000));
        var run = Task.Run(task.Execute);
        SpinWait.SpinUntil(() => ToolCalls().Count > 0, TimeSpan.FromSeconds(30)).ShouldBeTrue("dpm never started");

        task.Cancel();

        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(run, "the task didn't return after cancelling");
        (await run).ShouldBeFalse();
        _engine.Errors.ShouldContain(e => e.Code == DamlDiagnosticCodes.CancelledOrTimedOut && e.Message!.Contains("cancelled"));
        WaitUntilNoFakeDpmRuns();
    }

    [Fact, Trait("Category", "Process")]
    public void A_dpm_that_isnt_there_fails_before_anything_runs()
    {
        var task = Build(Command("build", ""));
        task.DpmPath = Path.Combine(Package, @"nope\dpm.exe");

        task.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.MissingTool);
        ToolCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Process")]
    public void Offline_overrides_install_permission_when_starting_dpm()
    {
        var task = Build(CreatesTheDar());
        task.Offline = "true";
        task.AllowToolInstall = "true";
        task.EnvironmentVariables = [.. task.EnvironmentVariables, "DPM_AUTO_INSTALL=true"];

        task.Execute().ShouldBeTrue();

        _engine.Errors.ShouldBeEmpty();
        ToolCalls().ShouldHaveSingleItem().Environment["DPM_AUTO_INSTALL"].ShouldBe("false");
    }

    [Fact, Trait("Category", "Process")]
    public void A_failing_script_is_an_error_at_the_script()
    {
        var command = Command("test", Captured("test-failing-script.stderr.txt"), exitCode: 1);
        command.Stdout = Captured("test-failing-script.stdout.txt");
        var task = Prepare(new DamlTestTask(), command);

        task.Execute().ShouldBeFalse();

        var error = _engine.Errors.ShouldHaveSingleItem();
        (error.Code, error.Subcategory, error.File, error.LineNumber).ShouldBe(("EQD0001", "Script", Path.Combine(Package, @"daml\Main.daml"), 47));
        ToolCalls().ShouldHaveSingleItem().Args.ShouldBe(["test"]);
    }

    // The lines -t:EqDamlTest is run for show without -v:d; the coverage report after them doesn't.
    [Fact, Trait("Category", "Process")]
    public void Each_script_of_a_test_run_shows_its_result_at_any_verbosity()
    {
        var command = Command("test", Captured("test-failing-script.stderr.txt"), exitCode: 1);
        command.Stdout = Captured("test-failing-script.stdout.txt");
        var task = Prepare(new DamlTestTask(), command);

        task.Execute();

        _engine.Messages.Where(m => m is not TaskCommandLineEventArgs && m.Importance == MessageImportance.High).Select(m => m.Message).ShouldBe(
            ["daml/Main.daml:failing: failed", "daml/Main.daml:setup: ok, 1 active contracts, 3 transactions."]);
        _engine.Messages.ShouldContain(m => m.Message == "Modules internal to this package:" && m.Importance == MessageImportance.Low);
    }

    [Fact, Trait("Category", "Process")]
    public void A_dpm_older_than_the_build_supports_stops_it_before_anything_runs()
    {
        var task = Build(Version("version: 1.0.10\n"), Command("build", ""));

        task.Execute().ShouldBeFalse();

        var error = _engine.Errors.ShouldHaveSingleItem();
        (error.Code, error.File).ShouldBe((DamlDiagnosticCodes.DpmTooOld, _engine.ProjectFileOfTaskNode));
        error.Message!.ShouldStartWith("dpm 1.0.10 is older than 1.0.22");
        ToolCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Process")]
    public void A_dpm_that_cant_say_its_version_stops_the_build()
    {
        var task = Build(Version("   3.5.2 \n * 3.6.0 \n"), Command("build", ""));

        task.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Message!.ShouldContain("It printed:\n3.5.2\n* 3.6.0");
        ToolCalls().ShouldBeEmpty();
    }

    // Asking dpm costs a process; a build of many projects asks once, until dpm itself changes.
    [Fact, Trait("Category", "Process")]
    public void The_version_is_asked_once_per_dpm_until_the_file_changes()
    {
        var created = Command("build", "Created .daml\\dist\\contracts-1.2.0.dar\n");

        Build(created).Execute().ShouldBeTrue();
        Build(created).Execute().ShouldBeTrue();
        VersionProbes().ShouldBe(1);

        File.SetLastWriteTimeUtc(_dpm.ExecutablePath, DateTime.UtcNow.AddMinutes(1));
        Build(created).Execute().ShouldBeTrue();
        VersionProbes().ShouldBe(2);
        ToolCalls().Count.ShouldBe(3);
    }

    [Fact, Trait("Category", "Process")]
    public void Skipping_the_preflight_asks_nothing_and_says_so()
    {
        var task = Build(Command("build", "Created .daml\\dist\\contracts-1.2.0.dar\n"));
        task.SkipPreflight = "true";

        task.Execute().ShouldBeTrue();

        VersionProbes().ShouldBe(0);
        var warning = _engine.Warnings.ShouldHaveSingleItem();
        (warning.Code, warning.Message).ShouldBe((DamlDiagnosticCodes.DpmTooOld, "EqDamlSkipPreflight is true, so the build doesn't check that dpm is new enough."));
    }

    private void WaitUntilNoFakeDpmRuns()
    {
        var call = ToolCalls().ShouldHaveSingleItem();
        call.ProcessId.ShouldBeGreaterThan(0);
        bool Running()
        {
            try
            {
                using var process = Process.GetProcessById(call.ProcessId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        SpinWait.SpinUntil(() => !Running(), TimeSpan.FromSeconds(10)).ShouldBeTrue("the fake dpm is still running");
    }
}
