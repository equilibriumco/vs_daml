using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Build;
using Daml.VisualStudio.Core.Processes;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Build;

public sealed class DpmRunnerTests : IDisposable
{
    private readonly FakeDpm _fake;
    private readonly string _package;
    private readonly DpmRunner _runner = new(new ProcessRunner());

    public DpmRunnerTests()
    {
        var scenario = new FakeDpmScenario();
        scenario.Commands.Add(Command(["build"], stderr: File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "build-output", "build-broken.stderr.txt")), exitCode: 1));
        scenario.Commands.Add(Command(["build", "--all"], stdout: "one\ntwo\n", stderr: "three\n"));
        scenario.Commands.Add(Command(["clean"], stderr: "Removed build artifacts.\n"));
        scenario.Commands.Add(Command(["test"], stdout: "Test Summary\n"));
        _fake = FakeDpm.Create(scenario);
        _package = Directory.CreateDirectory(Path.Combine(_fake.RootDirectory, "my package")).FullName;
    }

    public void Dispose() => _fake.Dispose();

    private static FakeDpmCommand Command(string[] args, string stdout = "", string stderr = "", int exitCode = 0)
    {
        var command = new FakeDpmCommand { Stdout = stdout, Stderr = stderr, ExitCode = exitCode };
        command.Args.AddRange(args);
        return command;
    }

    // The fake reads its scenario from the environment, so it runs through a wrapper that sets it,
    // the same way the real dpm.cmd wraps dpm.exe.
    private string Dpm()
    {
        var wrapper = Path.Combine(_fake.RootDirectory, "dpm.cmd");
        var set = string.Concat(_fake.Environment.Select(v => $"set \"{v.Key}={v.Value}\"\r\n"));
        File.WriteAllText(wrapper, "@echo off\r\n" + set + $"\"{_fake.ExecutablePath}\" %*\r\n", Encoding.ASCII);
        return wrapper;
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_build_reports_its_exit_code_and_the_diagnostics_dpm_printed()
    {
        var result = await _runner.RunAsync(Dpm(), DpmCommandKind.Build, _package, allPackages: false, onOutput: null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.Select(d => d.PrintedPath).ShouldBe(["daml/SyntaxError.daml", "daml/TypeError.daml", "daml/UnknownImport.daml"]);
        var call = _fake.ReadCalls().ShouldHaveSingleItem();
        call.Args.ShouldBe(["build"]);
        call.WorkingDirectory.ShouldBe(_package);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Output_is_handed_over_line_by_line_with_the_stream_it_came_from()
    {
        var lines = new List<DpmOutputLine>();

        var result = await _runner.RunAsync(Dpm(), DpmCommandKind.Build, _package, allPackages: true, lines.Add, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        lines.Where(l => l.Channel == DpmOutputChannel.Stdout).Select(l => l.Text).ShouldBe(["one", "two"]);
        lines.Where(l => l.Channel == DpmOutputChannel.Stderr).Select(l => l.Text).ShouldBe(["three"]);
        _fake.ReadCalls().Single().Args.ShouldBe(["build", "--all"]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(DpmCommandKind.Clean, "clean")]
    [InlineData(DpmCommandKind.Test, "test")]
    public async Task Clean_and_test_run_their_own_commands(DpmCommandKind kind, string argument)
    {
        var result = await _runner.RunAsync(Dpm(), kind, _package, allPackages: false, onOutput: null, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        result.Diagnostics.ShouldBeEmpty();
        _fake.ReadCalls().Single().Args.ShouldBe([argument]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Testing_every_package_is_refused_before_anything_starts()
    {
        await Should.ThrowAsync<ArgumentException>(() => _runner.RunAsync(Dpm(), DpmCommandKind.Test, _package, allPackages: true, onOutput: null, CancellationToken.None));

        _fake.ReadCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Cancelling_stops_the_run()
    {
        var hanging = Path.Combine(_fake.RootDirectory, "hang.cmd");
        File.WriteAllText(hanging, "@echo off\r\nping -n 120 127.0.0.1\r\n", Encoding.ASCII);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => _runner.RunAsync(hanging, DpmCommandKind.Build, _package, allPackages: false, onOutput: null, cancel.Token));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }
}
