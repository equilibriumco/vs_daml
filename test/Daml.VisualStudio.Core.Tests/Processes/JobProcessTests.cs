using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Processes;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Processes;

// Long-running processes are created inside a kill-on-close Job Object, so nothing they start
// can outlive them: closing the job (Dispose, or the owning process dying) ends the whole tree.
public sealed class JobProcessTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private readonly FakeDpm _fake;

    public JobProcessTests()
    {
        var scenario = new FakeDpmScenario();
        var command = new FakeDpmCommand { Stdout = "out ", Stderr = "err ", ExitCode = 3 };
        command.Args.Add("anything");
        scenario.Commands.Add(command);
        _fake = FakeDpm.Create(scenario);
    }

    public void Dispose() => _fake.Dispose();

    private string Wrapper(string folderName, string body)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_fake.RootDirectory, folderName)).FullName;
        var path = Path.Combine(folder, "dpm.cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n", Encoding.ASCII);
        return path;
    }

    private ProcessSpec Spec(string fileName, params string[] args) => new(fileName, args)
    {
        WorkingDirectory = _fake.RootDirectory,
        Environment = _fake.Environment.ToDictionary(p => p.Key, p => (string?)p.Value),
    };

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(ProcessRunnerTests.AwkwardArguments), MemberType = typeof(ProcessRunnerTests))]
    public async Task Arguments_reach_a_program_started_in_a_job_unchanged(string argument)
    {
        var wrapper = Wrapper("bin & zółw", $"\"{_fake.ExecutablePath}\" %*");

        using (var direct = new ProcessRunner().Start(Spec(_fake.ExecutablePath, "first", argument)))
        {
            await ExitCodeAsync(direct);
        }
        using (var throughCmd = new ProcessRunner().Start(Spec(wrapper, "first", argument)))
        {
            await ExitCodeAsync(throughCmd);
        }

        _fake.ReadCalls().Select(c => c.Args).ShouldAllBe(args => args.SequenceEqual(new[] { "first", argument }));
        _fake.ReadCalls().Count.ShouldBe(2);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Exit_code_streams_working_directory_and_environment_work_as_with_a_plain_process()
    {
        var work = Directory.CreateDirectory(Path.Combine(_fake.RootDirectory, "work dir")).FullName;
        var spec = Spec(_fake.ExecutablePath, "anything") with { WorkingDirectory = work };
        var environment = spec.Environment.ToDictionary(p => p.Key, p => p.Value);
        environment["DAML_SET_BY_TEST"] = "set";
        using var running = new ProcessRunner().Start(spec with { Environment = environment });

        running.StandardInput.Close();
        var stdout = new StreamReader(running.StandardOutput).ReadToEndAsync();
        var stderr = new StreamReader(running.StandardError).ReadToEndAsync();

        (await ExitCodeAsync(running)).ShouldBe(3);
        (await stdout).ShouldBe("out ");
        (await stderr).ShouldBe("err ");
        var call = _fake.ReadCalls().Single();
        call.WorkingDirectory.ShouldBe(work);
        call.Environment["DAML_SET_BY_TEST"].ShouldBe("set");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Disposing_ends_the_process_and_everything_it_started()
    {
        var wrapper = Wrapper("hang", "ping -n 120 127.0.0.1");
        var running = new ProcessRunner().Start(Spec(wrapper));
        using var root = Process.GetProcessById(running.Id);
        var children = await WaitForChildrenAsync(running.Id);

        running.Dispose();

        HasExited(root).ShouldBeTrue();
        children.ShouldAllBe(c => HasExited(c));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Killing_the_tree_terminates_the_job()
    {
        var wrapper = Wrapper("hang", "ping -n 120 127.0.0.1");
        using var running = new ProcessRunner().Start(Spec(wrapper));
        var children = await WaitForChildrenAsync(running.Id);

        running.KillTree();

        (await ExitCodeAsync(running)).ShouldNotBe(0);
        children.ShouldAllBe(c => HasExited(c));
    }

    [Fact, Trait("Category", "Unit")]
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

        Should.Throw<InvalidOperationException>(() => runner.Start(Spec(Wrapper("hang", "ping -n 120 127.0.0.1"))));

        created.ShouldNotBe(0);
        IsGone(created).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
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

    [Fact, Trait("Category", "Unit")]
    public void A_working_directory_that_does_not_exist_fails_the_start()
    {
        var spec = Spec(_fake.ExecutablePath, "anything") with { WorkingDirectory = Path.Combine(_fake.RootDirectory, "missing") };

        Should.Throw<Win32Exception>(() => new ProcessRunner().Start(spec));

        _fake.ReadCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_missing_program_fails_before_anything_is_created()
    {
        var stages = new List<ProcessRunner.StartStage>();
        var runner = new ProcessRunner((stage, _) => stages.Add(stage));

        Should.Throw<FileNotFoundException>(() => runner.Start(Spec(Path.Combine(_fake.RootDirectory, "nope.exe"))));

        stages.ShouldBeEmpty();
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
    private static Task<int> ExitCodeAsync(IRunningProcess process) => WithTimeout(process.Exited);
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
}
