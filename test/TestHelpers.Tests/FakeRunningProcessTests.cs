using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.TestHelpers.Tests;

public class FakeRunningProcessTests
{
    [Fact, Trait("Category", "Unit")]
    public void Disposal_cancels_an_unfinished_exit_and_late_exit_cannot_replace_it()
    {
        var process = new FakeRunningProcess(new ProcessSpec("fake", []));
        process.Dispose();
        process.Exit(0);
        process.Exited.IsCanceled.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Disposal_preserves_an_exit_code_already_reported()
    {
        var process = new FakeRunningProcess(new ProcessSpec("fake", []));
        process.Exit(7);
        process.Dispose();
        (await process.Exited).ShouldBe(7);
    }
}
