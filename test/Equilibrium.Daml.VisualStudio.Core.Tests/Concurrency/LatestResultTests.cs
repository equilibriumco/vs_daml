using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Concurrency;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Concurrency;

public class LatestResultTests
{
    [Fact, Trait("Category", "Unit")]
    public async Task A_slow_old_computation_cannot_overwrite_a_new_result()
    {
        using var results = new LatestResult<string>();
        var oldGeneration = results.Generation;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The gate is owned by this test; no synchronization context is needed to release it.
#pragma warning disable VSTHRD003
        var oldWork = Task.Run(async () => { await release.Task; return results.TryPublish(oldGeneration, "old"); });
#pragma warning restore VSTHRD003
        results.Invalidate();
        results.TryPublish(results.Generation, "new").ShouldBeTrue();
        release.SetResult(true);

        (await oldWork).ShouldBeFalse();
        results.Current.ShouldBe("new");
    }

    [Fact, Trait("Category", "Unit")]
    public void Work_finishing_after_disposal_cannot_retain_its_result()
    {
        var results = new LatestResult<string>();
        var generation = results.Generation;
        results.Dispose();
        results.TryPublish(generation, "late").ShouldBeFalse();
        results.Current.ShouldBeNull();
    }
}
