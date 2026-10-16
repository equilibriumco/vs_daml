using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Tasks;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Tasks;

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
            ("DAML0001", "parser", Path.Combine(Package, @"daml\SyntaxError.daml"), 6, 1, 6, 1),
            ("DAML0001", "typecheck", Path.Combine(Package, @"daml\TypeError.daml"), 5, 9, 5, 20),
            ("DAML0001", "not found", Path.Combine(Package, @"daml\UnknownImport.daml"), 4, 8, 4, 23),
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
        error.Message!.ShouldContain("DamlProcessTimeoutSeconds");
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
    public void Settings_that_contradict_each_other_fail_before_anything_runs()
    {
        var task = Build(Command("build", ""));
        task.Offline = "true";
        task.AllowToolInstall = "true";

        task.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        ToolCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Process")]
    public void A_failing_script_is_an_error_at_the_script()
    {
        var command = Command("test", Captured("test-failing-script.stderr.txt"), exitCode: 1);
        command.Stdout = Captured("test-failing-script.stdout.txt");
        var task = Prepare(new DamlTestTask(), command);

        task.Execute().ShouldBeFalse();

        var error = _engine.Errors.ShouldHaveSingleItem();
        (error.Code, error.Subcategory, error.File, error.LineNumber).ShouldBe(("DAML0001", "Script", Path.Combine(Package, @"daml\Main.daml"), 47));
        ToolCalls().ShouldHaveSingleItem().Args.ShouldBe(["test"]);
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
        (warning.Code, warning.Message).ShouldBe((DamlDiagnosticCodes.DpmTooOld, "DamlSkipPreflight is true, so the build doesn't check that dpm is new enough."));
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
