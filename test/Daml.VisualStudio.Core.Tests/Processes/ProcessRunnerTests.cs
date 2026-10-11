using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Processes;
using Shouldly;
using Xunit;
using ProcessResult = Daml.VisualStudio.Core.Processes.ProcessResult;

namespace Daml.VisualStudio.Core.Tests.Processes;

// Runs real processes: the fake dpm directly, and through a .cmd wrapper shaped like the
// dpm.cmd that the dpm installer puts on PATH.
public sealed class ProcessRunnerTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private readonly FakeDpm _fake;
    private readonly ProcessRunner _runner = new();

    public ProcessRunnerTests()
    {
        var scenario = new FakeDpmScenario();
        var any = new FakeDpmCommand { Stdout = "out ", Stderr = "err ", ExitCode = 3 };
        any.Args.Add("anything");
        scenario.Commands.Add(any);
        _fake = FakeDpm.Create(scenario);
    }

    public void Dispose() => _fake.Dispose();

    private string Wrapper(string folderName, string body)
    {
        var folder = Path.Combine(_fake.RootDirectory, folderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "dpm.cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n", Encoding.ASCII);
        return path;
    }

    private ProcessSpec Spec(string fileName, params string[] args) => new(fileName, args)
    {
        WorkingDirectory = _fake.RootDirectory,
        Environment = _fake.Environment.ToDictionary(p => p.Key, p => (string?)p.Value),
        Timeout = Generous,
    };

    public static TheoryData<string> AwkwardArguments => new()
    {
        "plain",
        "",
        "with space",
        "quote\"inside",
        @"trailing\",
        @"C:\my dir\",
        @"back\\""slash",
        "a&b",
        "x|y",
        "<in>",
        "(paren)",
        "a^b",
        "100%",
        "%PATH%",
        "!PATH!",
        "zażółć 日本",
    };

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(AwkwardArguments))]
    public async Task An_executable_receives_each_argument_unchanged(string argument)
    {
        await _runner.RunAsync(Spec(_fake.ExecutablePath, "first", argument, "last"), CancellationToken.None);

        _fake.ReadCalls().Single().Args.ShouldBe(["first", argument, "last"]);
    }

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(AwkwardArguments))]
    public async Task A_cmd_wrapper_passes_each_argument_on_unchanged(string argument)
    {
        // Folder name with a space, an ampersand and non-ASCII letters, like a user profile can have.
        var wrapper = Wrapper("bin & zółw", $"\"{_fake.ExecutablePath}\" %*");

        await _runner.RunAsync(Spec(wrapper, "first", argument, "last"), CancellationToken.None);

        _fake.ReadCalls().Single().Args.ShouldBe(["first", argument, "last"]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Output_error_and_exit_code_are_captured()
    {
        var result = await _runner.RunAsync(Spec(_fake.ExecutablePath, "anything"), CancellationToken.None);

        result.ShouldBe(new ProcessResult(3, "out ", "err "));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Exit_code_and_output_come_back_through_a_cmd_wrapper()
    {
        var wrapper = Wrapper("bin", $"\"{_fake.ExecutablePath}\" %*");

        var result = await _runner.RunAsync(Spec(wrapper, "anything"), CancellationToken.None);

        result.ShouldBe(new ProcessResult(3, "out ", "err "));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Working_directory_and_environment_are_applied()
    {
        var work = Directory.CreateDirectory(Path.Combine(_fake.RootDirectory, "work dir")).FullName;
        Environment.SetEnvironmentVariable("DAML_INHERITED_BY_TEST", "kept");
        Environment.SetEnvironmentVariable("DPM_REMOVED_BY_TEST", "gone");
        try
        {
            var spec = Spec(_fake.ExecutablePath, "x") with { WorkingDirectory = work };
            var environment = spec.Environment.ToDictionary(p => p.Key, p => p.Value);
            environment["DAML_SET_BY_TEST"] = "set";
            environment["DPM_REMOVED_BY_TEST"] = null;

            await _runner.RunAsync(spec with { Environment = environment }, CancellationToken.None);

            var call = _fake.ReadCalls().Single();
            call.WorkingDirectory.ShouldBe(work);
            call.Environment["DAML_SET_BY_TEST"].ShouldBe("set");
            call.Environment["DAML_INHERITED_BY_TEST"].ShouldBe("kept");
            call.Environment.Keys.ShouldNotContain("DPM_REMOVED_BY_TEST");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DAML_INHERITED_BY_TEST", null);
            Environment.SetEnvironmentVariable("DPM_REMOVED_BY_TEST", null);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Timeout_kills_the_wrapper_and_what_it_started()
    {
        // cmd.exe -> ping.exe: killing only cmd.exe would leave ping running and holding the pipes.
        var wrapper = Wrapper("hang", "ping -n 120 127.0.0.1");
        var watch = Stopwatch.StartNew();

        var timeout = await Should.ThrowAsync<TimeoutException>(
            () => _runner.RunAsync(Spec(wrapper) with { Timeout = TimeSpan.FromSeconds(2) }, CancellationToken.None));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        timeout.Message.ShouldContain("dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Cancellation_stops_the_run()
    {
        var wrapper = Wrapper("hang", "ping -n 120 127.0.0.1");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => _runner.RunAsync(Spec(wrapper), cancel.Token));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Killing_the_tree_ends_the_wrapper_and_its_children()
    {
        var wrapper = Wrapper("hang", "ping -n 120 127.0.0.1");
        using var running = _runner.Start(Spec(wrapper));
        var children = await WaitForChildrenAsync(running.Id);

        running.KillTree();

        (await WithTimeout(running.Exited)).ShouldNotBe(0);
        children.ShouldAllBe(c => HasExited(c));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_started_process_exposes_its_standard_streams()
    {
        var sort = Path.Combine(Environment.SystemDirectory, "sort.exe");
        using var running = _runner.Start(new ProcessSpec(sort, []) { WorkingDirectory = _fake.RootDirectory });

        using (var writer = new StreamWriter(running.StandardInput, Encoding.ASCII))
        {
            await writer.WriteAsync("pear\r\napple\r\n");
        }
        using var reader = new StreamReader(running.StandardOutput, Encoding.ASCII);
        var output = await reader.ReadToEndAsync();

        output.ShouldBe("apple\r\npear\r\n");
        (await WithTimeout(running.Exited)).ShouldBe(0);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_missing_program_fails_to_start()
    {
        var missing = Path.Combine(_fake.RootDirectory, "nope.exe");

        await Should.ThrowAsync<FileNotFoundException>(() => _runner.RunAsync(Spec(missing), CancellationToken.None));
    }

    private static async Task<IReadOnlyList<Process>> WaitForChildrenAsync(int parentId)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (DateTime.UtcNow < deadline)
        {
            var children = ProcessTree.Descendants(parentId);
            if (children.Count > 0)
            {
                return children;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Process {parentId} started no children.");
    }

    // The process's exit isn't tied to any synchronization context, so awaiting it here can't deadlock.
#pragma warning disable VSTHRD003
    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(Generous)) != task)
        {
            throw new TimeoutException("The process didn't exit.");
        }
        return await task;
    }
#pragma warning restore VSTHRD003

    private static bool HasExited(Process process)
    {
        process.WaitForExit(10_000);
        return process.HasExited;
    }
}
