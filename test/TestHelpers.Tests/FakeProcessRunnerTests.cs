using System;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.TestHelpers.Tests;

// What tests built on the fake rely on: nothing passes by an unexpected command, and a test can
// change a default answer.
public class FakeProcessRunnerTests
{
    [Fact, Trait("Category", "Unit")]
    public async Task A_call_nobody_scripted_throws_and_is_still_recorded()
    {
        var runner = new FakeProcessRunner().On("dpm", ["version"], "1.0.22");

        var error = await Should.ThrowAsync<InvalidOperationException>(() => runner.RunAsync(new ProcessSpec("dpm", ["build"]), CancellationToken.None));

        error.Message.ShouldContain("dpm build");
        runner.Calls.ShouldHaveSingleItem().Arguments.ShouldBe(["build"]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_later_entry_for_the_same_command_wins()
    {
        var runner = new FakeProcessRunner()
            .On("dpm", ["version"], "1.0.20")
            .On("dpm", ["version"], "1.0.22");

        (await runner.RunAsync(new ProcessSpec("dpm", ["version"]), CancellationToken.None)).Stdout.ShouldBe("1.0.22");
    }

    [Fact, Trait("Category", "Unit")]
    public void Started_processes_are_recorded_and_a_scripted_failure_stops_them()
    {
        var runner = new FakeProcessRunner();
        runner.Start(new ProcessSpec("damlc", ["ide"]));
        runner.FailStarts(new InvalidOperationException("no damlc"));

        Should.Throw<InvalidOperationException>(() => runner.Start(new ProcessSpec("damlc", ["ide"])));

        runner.Started.Count.ShouldBe(1);
        runner.Calls.Count.ShouldBe(2);
    }
}
