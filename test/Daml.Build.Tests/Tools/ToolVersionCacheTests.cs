using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Tools;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Tools;

public class ToolVersionCacheTests
{
    [Fact, Trait("Category", "Unit")]
    public async Task Cancelling_the_probe_owner_does_not_cancel_another_build()
    {
        var tool = Path.GetTempFileName();
        try
        {
            var cache = new ToolVersionCache();
            using var owner = new CancellationTokenSource();
            var first = cache.OutputAsync(tool, async () =>
            {
                await Task.Delay(Timeout.Infinite, owner.Token);
                return ((IReadOnlyList<string>)new[] { "unreachable" }, true);
            }, owner.Token);
            var other = cache.OutputAsync(tool, () => Task.FromResult(((IReadOnlyList<string>)new[] { "retried" }, true)));
            owner.CancelAfter(0);

#pragma warning disable VSTHRD003 // The assertion observes the operation started in this test.
            await Should.ThrowAsync<OperationCanceledException>(async () => await first);
#pragma warning restore VSTHRD003
            (await other).Output.ShouldBe(["retried"]);
        }
        finally { File.Delete(tool); }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_cancelled_waiter_leaves_the_shared_probe_running()
    {
        var tool = Path.GetTempFileName();
        try
        {
            var cache = new ToolVersionCache();
            var answer = new TaskCompletionSource<(IReadOnlyList<string>, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // This test completes the shared probe after the waiter cancels.
            var first = cache.OutputAsync(tool, () => answer.Task);
#pragma warning restore VSTHRD003
            using var waiter = new CancellationTokenSource();
            var second = cache.OutputAsync(tool, () => throw new InvalidOperationException("The probe is already running."), waiter.Token);
            waiter.CancelAfter(0);

#pragma warning disable VSTHRD003 // The assertion observes the operation started in this test.
            await Should.ThrowAsync<OperationCanceledException>(async () => await second);
#pragma warning restore VSTHRD003
            first.IsCompleted.ShouldBeFalse();
            answer.SetResult((new[] { "version" }, true));
            (await first).Succeeded.ShouldBeTrue();
        }
        finally { File.Delete(tool); }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Concurrent_callers_share_one_probe_and_a_failure_is_retried()
    {
        var tool = Path.GetTempFileName();
        try
        {
            var cache = new ToolVersionCache();
            var completion = new TaskCompletionSource<(IReadOnlyList<string>, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
#pragma warning disable VSTHRD003 // The test releases this completion source after all callers have joined.
            Task<(IReadOnlyList<string>, bool)> Ask() { calls++; return completion.Task; }
#pragma warning restore VSTHRD003
            var waiting = Enumerable.Range(0, 20).Select(_ => cache.OutputAsync(tool, Ask)).ToArray();
            calls.ShouldBe(1);
            completion.SetResult((new[] { "failed" }, false));
            (await Task.WhenAll(waiting)).ShouldAllBe(a => !a.Succeeded);
            var recovered = await cache.OutputAsync(tool, () => Task.FromResult(((IReadOnlyList<string>)new[] { "1.0.22" }, true)));
            recovered.Output.ShouldBe(["1.0.22"]);
            (await cache.OutputAsync(tool, () => throw new InvalidOperationException("Cached answers must not probe."))).Succeeded.ShouldBeTrue();
        }
        finally { File.Delete(tool); }
    }
}
