using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Bindings;

// The background refreshes: after two quiet seconds, one at a time, a stale run cancelled
// and run again, nothing while Visual Studio builds, and what the build covered left to it.
public sealed class BindingSyncSchedulerTests : IDisposable
{
    private static readonly BindingRefreshTarget App = new(@"C:\sln\App\App.csproj", null);
    private static readonly BindingRefreshTarget Web = new(@"C:\sln\Web\Web.csproj", "net10.0");

    private readonly FakeTimeProvider _time = new();
    private readonly FakeRefresher _refresher = new();
    private readonly BindingSyncScheduler _scheduler;
    private readonly List<BindingSyncStatus> _statuses = [];

    public BindingSyncSchedulerTests()
    {
        _scheduler = new BindingSyncScheduler(_refresher, _time);
        _scheduler.StatusChanged += (_, status) =>
        {
            lock (_statuses)
            {
                _statuses.Add(status);
            }
        };
    }

    public void Dispose() => _scheduler.Dispose();

    private BindingSyncState[] StatesOf(BindingRefreshTarget target)
    {
        lock (_statuses)
        {
            return [.. _statuses.Where(s => s.Target == target).Select(s => s.State)];
        }
    }

    private async Task QuietAsync(double seconds = 2)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        await FakeRefresher.SettleAsync();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_request_is_refreshed_after_two_quiet_seconds()
    {
        _scheduler.Request([App]);
        await QuietAsync(1.9);
        _refresher.Started.ShouldBeEmpty();

        await QuietAsync(0.1);
        _refresher.Complete(App, new BindingRefreshResult(true, null));
        await FakeRefresher.SettleAsync();

        _refresher.Started.ShouldBe([App]);
        StatesOf(App).ShouldBe([BindingSyncState.Stale, BindingSyncState.Updating, BindingSyncState.Ready]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Saves_in_a_row_make_one_refresh_per_project_one_at_a_time()
    {
        _scheduler.Request([App]);
        await QuietAsync(1);
        _scheduler.Request([App, Web]);
        await QuietAsync();

        _refresher.Started.ShouldBe([App]);
        _refresher.Complete(App, new BindingRefreshResult(true, null));
        await FakeRefresher.SettleAsync();
        _refresher.Started.ShouldBe([App, Web]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_project_asked_for_again_while_refreshing_is_cancelled_and_refreshed_anew()
    {
        _scheduler.Request([App]);
        await QuietAsync();
        var first = _refresher.TokenOf(App);

        _scheduler.Request([App]);

        first.IsCancellationRequested.ShouldBeTrue();
        await QuietAsync();
        _refresher.Started.ShouldBe([App, App]);
    }

    // The build regenerates what was asked for before it; only what came during it is still due after.
    [Fact, Trait("Category", "Unit")]
    public async Task A_build_holds_refreshes_back_and_takes_over_those_asked_for_before_it()
    {
        _scheduler.Request([App]);
        _scheduler.BuildStarted();
        await QuietAsync();
        _scheduler.Request([Web]);
        await QuietAsync();
        _refresher.Started.ShouldBeEmpty();

        _scheduler.BuildFinished();
        await FakeRefresher.SettleAsync();

        _refresher.Started.ShouldBe([Web]);
        StatesOf(App).Last().ShouldBe(BindingSyncState.Ready);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_build_stops_a_running_refresh()
    {
        _scheduler.Request([App]);
        await QuietAsync();
        var running = _refresher.TokenOf(App);

        _scheduler.BuildStarted();
        running.IsCancellationRequested.ShouldBeTrue();
        _scheduler.BuildFinished();
        await QuietAsync();

        _refresher.Started.ShouldBe([App]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failure_is_reported_with_its_reason_and_the_next_save_tries_again()
    {
        _scheduler.Request([App]);
        await QuietAsync();
        _refresher.Complete(App, new BindingRefreshResult(false, "Main.daml(3,1): error DAML0001: parse error"));
        await FakeRefresher.SettleAsync();

        var failed = _statuses.Last();
        (failed.State, failed.Message).ShouldBe((BindingSyncState.Failed, "Main.daml(3,1): error DAML0001: parse error"));
        _scheduler.Request([App]);
        await QuietAsync();
        _refresher.Started.ShouldBe([App, App]);
    }

    // Another solution, or projects added or removed: nothing queued still applies.
    [Fact, Trait("Category", "Unit")]
    public async Task Clearing_cancels_the_running_refresh_and_forgets_the_rest()
    {
        _scheduler.Request([App, Web]);
        await QuietAsync();
        var running = _refresher.TokenOf(App);

        _scheduler.Clear();
        running.IsCancellationRequested.ShouldBeTrue();
        await QuietAsync();

        _refresher.Started.ShouldBe([App]);
    }

    // Each refresh waits until the test completes it, or is cancelled.
    private sealed class FakeRefresher : IBindingRefresher
    {
        private readonly object _gate = new();
        private readonly List<(BindingRefreshTarget Target, CancellationToken Token, TaskCompletionSource<BindingRefreshResult> Result)> _runs = [];

        public BindingRefreshTarget[] Started
        {
            get
            {
                lock (_gate)
                {
                    return [.. _runs.Select(r => r.Target)];
                }
            }
        }

        public async Task<BindingRefreshResult> RefreshAsync(BindingRefreshTarget target, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<BindingRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _runs.Add((target, cancellationToken, result));
            }
            using (cancellationToken.Register(() => result.TrySetCanceled()))
            {
                return await result.Task.ConfigureAwait(false);
            }
        }

        public CancellationToken TokenOf(BindingRefreshTarget target)
        {
            lock (_gate)
            {
                return _runs.Last(r => r.Target == target).Token;
            }
        }

        public void Complete(BindingRefreshTarget target, BindingRefreshResult result)
        {
            lock (_gate)
            {
                _runs.Last(r => r.Target == target).Result.TrySetResult(result);
            }
        }

        // Lets the scheduler's continuations run after the clock moved or a refresh ended.
        public static async Task SettleAsync()
        {
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
    }
}
