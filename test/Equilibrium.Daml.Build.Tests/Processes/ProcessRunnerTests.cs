using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Processes;

// Runs real processes: the fake dpm directly, and through a .cmd wrapper shaped like the
// dpm.cmd that the dpm installer puts on PATH. Every process is created inside a kill-on-close
// Job Object, so nothing it starts can outlive it: closing the job (Dispose, or the owning
// process dying) ends the whole tree.
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

    // Folder name with a space, an ampersand and non-ASCII letters, like a user profile can have.
    private string ForwardingWrapper() => Wrapper("bin & zółw", $"\"{_fake.ExecutablePath}\" %*");

    private string HangingWrapper() => Wrapper("hang", "ping -n 120 127.0.0.1");

    private ProcessSpec Spec(string fileName, params string[] args) => new(fileName, args)
    {
        WorkingDirectory = _fake.RootDirectory,
        Environment = _fake.Environment.ToDictionary(p => p.Key, p => (string?)p.Value),
        Timeout = Generous,
    };

    private static readonly string[] AwkwardArguments =
    [
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
    ];

    // Each argument straight to the executable, and once more through cmd's %* forwarding.
    public static TheoryData<string, bool> AwkwardArgumentsBothWays()
    {
        var data = new TheoryData<string, bool>();
        foreach (var argument in AwkwardArguments)
        {
            data.Add(argument, false);
            data.Add(argument, true);
        }
        return data;
    }

    [Theory, Trait("Category", "Process")]
    [MemberData(nameof(AwkwardArgumentsBothWays))]
    public async Task Each_argument_arrives_unchanged(string argument, bool viaCmd)
    {
        var program = viaCmd ? ForwardingWrapper() : _fake.ExecutablePath;

        await _runner.RunAsync(Spec(program, "first", argument, "last"), CancellationToken.None);

        _fake.ReadCalls().Single().Args.ShouldBe(["first", argument, "last"]);
    }

    [Theory, Trait("Category", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_error_and_exit_code_are_captured(bool viaCmd)
    {
        var program = viaCmd ? ForwardingWrapper() : _fake.ExecutablePath;

        var result = await _runner.RunAsync(Spec(program, "anything"), CancellationToken.None);

        result.ShouldBe(new ProcessResult(3, "out ", "err "));
    }

    [Fact, Trait("Category", "Process")]
    public async Task Working_directory_and_environment_are_applied()
    {
        var work = Directory.CreateDirectory(Path.Combine(_fake.RootDirectory, "work dir")).FullName;
        // Inheritance can only be shown with a variable of this process; these two names belong
        // to this test alone, and no other test asserts on their absence.
        Environment.SetEnvironmentVariable("EQDAML_INHERITED_BY_TEST", "kept");
        Environment.SetEnvironmentVariable("DPM_REMOVED_BY_TEST", "gone");
        try
        {
            var spec = Spec(_fake.ExecutablePath, "x") with { WorkingDirectory = work };
            var environment = spec.Environment.ToDictionary(p => p.Key, p => p.Value);
            environment["EQDAML_SET_BY_TEST"] = "set";
            environment["DPM_REMOVED_BY_TEST"] = null;

            await _runner.RunAsync(spec with { Environment = environment }, CancellationToken.None);

            var call = _fake.ReadCalls().Single();
            call.WorkingDirectory.ShouldBe(work);
            call.Environment["EQDAML_SET_BY_TEST"].ShouldBe("set");
            call.Environment["EQDAML_INHERITED_BY_TEST"].ShouldBe("kept");
            call.Environment.Keys.ShouldNotContain("DPM_REMOVED_BY_TEST");
        }
        finally
        {
            Environment.SetEnvironmentVariable("EQDAML_INHERITED_BY_TEST", null);
            Environment.SetEnvironmentVariable("DPM_REMOVED_BY_TEST", null);
        }
    }

    [Fact, Trait("Category", "Process")]
    public async Task Timeout_kills_the_wrapper_and_what_it_started()
    {
        // cmd.exe -> ping.exe: killing only cmd.exe would leave ping running and holding the pipes.
        var watch = Stopwatch.StartNew();

        var timeout = await Should.ThrowAsync<TimeoutException>(
            () => _runner.RunAsync(Spec(HangingWrapper()) with { Timeout = TimeSpan.FromSeconds(2) }, CancellationToken.None));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        timeout.Message.ShouldContain("dpm.cmd");
    }

    [Fact, Trait("Category", "Process")]
    public async Task Cancellation_stops_the_run()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => _runner.RunAsync(Spec(HangingWrapper()), cancel.Token));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact, Trait("Category", "Process")]
    public async Task Killing_the_tree_ends_the_wrapper_and_its_children()
    {
        using var running = _runner.Start(Spec(HangingWrapper()));
        var children = await WaitForChildrenAsync(running.Id);

        running.KillTree();

        (await ExitCodeAsync(running)).ShouldNotBe(0);
        children.ShouldAllBe(c => HasExited(c));
    }

    [Fact, Trait("Category", "Process")]
    public async Task Disposing_ends_the_process_and_everything_it_started()
    {
        var running = _runner.Start(Spec(HangingWrapper()));
        using var root = Process.GetProcessById(running.Id);
        var children = await WaitForChildrenAsync(running.Id);

        running.Dispose();

        HasExited(root).ShouldBeTrue();
        children.ShouldAllBe(c => HasExited(c));
        running.Exited.IsCanceled.ShouldBeTrue("nobody will learn the exit code of a disposed process");
    }

    [Fact, Trait("Category", "Process")]
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
        (await ExitCodeAsync(running)).ShouldBe(0);
    }

    [Fact, Trait("Category", "Process")]
    public void A_failure_right_after_creation_kills_the_new_process()
    {
        var created = 0;
        var runner = new ProcessRunner((stage, processId) =>
        {
            if (stage == ProcessRunner.StartStage.Created)
            {
                created = processId;
                throw new InvalidOperationException("injected");
            }
        });

        Should.Throw<InvalidOperationException>(() => runner.Start(Spec(HangingWrapper())));

        created.ShouldNotBe(0);
        IsGone(created).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Process")]
    public void A_failure_before_creation_starts_nothing()
    {
        var stages = new List<ProcessRunner.StartStage>();
        var runner = new ProcessRunner((stage, _) =>
        {
            stages.Add(stage);
            if (stage == ProcessRunner.StartStage.JobCreated)
            {
                throw new InvalidOperationException("injected");
            }
        });

        Should.Throw<InvalidOperationException>(() => runner.Start(Spec(_fake.ExecutablePath, "anything")));

        stages.ShouldBe([ProcessRunner.StartStage.JobCreated]);
        _fake.ReadCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Process")]
    public void A_working_directory_that_does_not_exist_fails_the_start_and_names_it()
    {
        var missing = Path.Combine(_fake.RootDirectory, "missing");
        var spec = Spec(_fake.ExecutablePath, "anything") with { WorkingDirectory = missing };

        Should.Throw<Win32Exception>(() => _runner.Start(spec)).Message.ShouldContain(missing);

        _fake.ReadCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Process")]
    public async Task A_missing_program_fails_before_anything_is_created()
    {
        var stages = new List<ProcessRunner.StartStage>();
        var runner = new ProcessRunner((stage, _) => stages.Add(stage));
        var missing = Path.Combine(_fake.RootDirectory, "nope.exe");

        Should.Throw<FileNotFoundException>(() => runner.Start(Spec(missing)));
        await Should.ThrowAsync<FileNotFoundException>(() => runner.RunAsync(Spec(missing), CancellationToken.None));

        stages.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_program_named_without_its_path_is_refused()
    {
        Should.Throw<ArgumentException>(() => _runner.Start(new ProcessSpec("dpm.exe", [])));
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
    private static async Task<int> ExitCodeAsync(IRunningProcess process)
    {
        if (await Task.WhenAny(process.Exited, Task.Delay(Generous)) != process.Exited)
        {
            throw new TimeoutException("The process didn't exit.");
        }
        return await process.Exited;
    }
#pragma warning restore VSTHRD003

    private static bool HasExited(Process process)
    {
        process.WaitForExit(10_000);
        return process.HasExited;
    }

    private static bool IsGone(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return HasExited(process);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    // Both pipes well past the 4 KB a pipe buffers, with text that isn't ASCII: a runner that
    // read one pipe to the end before the other would deadlock here.
    [Fact, Trait("Category", "Process")]
    public async Task Large_output_on_both_pipes_is_read_whole_and_decoded_as_utf8()
    {
        var scenario = new FakeDpmScenario();
        var big = new FakeDpmCommand { Stdout = new string('x', 1_000_000) + "zażółć 日本", Stderr = new string('y', 700_000) + "ü", ExitCode = 0 };
        big.Args.Add("big");
        scenario.Commands.Add(big);
        using var fake = FakeDpm.Create(scenario);

        var result = await _runner.RunAsync(new ProcessSpec(fake.ExecutablePath, ["big"])
        {
            WorkingDirectory = fake.RootDirectory,
            Environment = fake.Environment.ToDictionary(p => p.Key, p => (string?)p.Value),
            Timeout = Generous,
        }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Stdout.Length.ShouldBe(1_000_000 + "zażółć 日本".Length);
        result.Stdout.ShouldEndWith("zażółć 日本");
        result.Stderr.ShouldEndWith("ü");
        result.Stderr.Length.ShouldBe(700_001);
    }

    // The streams must read without parking a thread (overlapped pipes), and once the child is
    // running nothing inheritable may be left in this process, or a process started elsewhere
    // at the wrong moment would inherit the child's pipe ends and keep them open.
    [Fact, Trait("Category", "Process")]
    public async Task The_pipes_are_overlapped_and_nothing_inheritable_stays_behind()
    {
        using var process = _runner.Start(Spec(_fake.ExecutablePath, "anything"));

        foreach (var stream in new[] { process.StandardInput, process.StandardOutput, process.StandardError })
        {
            var pipe = stream.ShouldBeOfType<NamedPipeServerStream>();
            pipe.IsAsync.ShouldBeTrue();
            GetHandleInformation(pipe.SafePipeHandle.DangerousGetHandle(), out var flags).ShouldBeTrue();
            (flags & HandleFlagInherit).ShouldBe(0u, "our end of the pipe isn't inheritable");
        }
        (await process.Exited).ShouldBe(3);
        (await new StreamReader(process.StandardOutput).ReadToEndAsync()).ShouldBe("out ");
    }

    private const uint HandleFlagInherit = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);
}
