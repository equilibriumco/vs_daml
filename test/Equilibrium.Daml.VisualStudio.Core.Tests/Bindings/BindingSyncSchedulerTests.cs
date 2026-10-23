using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Bindings;

// The background refreshes: after two quiet seconds, one at a time, a stale run cancelled
// and run again, with pending work verified after Visual Studio builds. The scheduler resumes
// on the thread pool after the clock moves, so the tests wait for what it reports.
public sealed class BindingSyncSchedulerTests : IDisposable
{
    private static readonly BindingRefreshTarget App = new(@"C:\sln\App\App.csproj", null);
    private static readonly BindingRefreshTarget Web = new(@"C:\sln\Web\Web.csproj", "net10.0");
    private static readonly BindingRefreshResult Succeeded = new(true, null);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeRefresher _refresher = new();
    private readonly BindingSyncScheduler _scheduler;
    private readonly List<(BindingSyncStatus Status, DateTimeOffset At)> _statuses = [];

    public BindingSyncSchedulerTests()
    {
        _scheduler = new BindingSyncScheduler(_refresher, _time);
        _scheduler.StatusChanged += (_, status) =>
        {
            lock (_statuses)
            {
                _statuses.Add((status, _time.GetUtcNow()));
            }
        };
    }

    public void Dispose() => _scheduler.Dispose();

    private (BindingSyncStatus Status, DateTimeOffset At)[] Statuses()
    {
        lock (_statuses)
        {
            return [.. _statuses];
        }
    }

    private BindingSyncState[] StatesOf(BindingRefreshTarget target) =>
        [.. Statuses().Where(s => s.Status.Target == target).Select(s => s.Status.State)];

    private void Quiet(double seconds = 2) => _time.Advance(TimeSpan.FromSeconds(seconds));

    private Task StartedAsync(params BindingRefreshTarget[] runs) =>
        Wait.UntilAsync(() => _refresher.Started.SequenceEqual(runs), $"refreshes of {string.Join(", ", runs.Select(r => r.Project))}");

    private Task StatesAsync(BindingRefreshTarget target, params BindingSyncState[] states) =>
        Wait.UntilAsync(() => StatesOf(target).SequenceEqual(states), $"{target.Project} to go {string.Join(", ", states)}");

    // Gives a refresh that should not start the chance to start anyway. Only a broken scheduler
    // can be caught out by it; a slow machine can't make a correct one fail.
    private static Task GraceAsync() => Task.Delay(100);

    [Fact, Trait("Category", "Unit")]
    public async Task A_request_is_refreshed_after_two_quiet_seconds()
    {
        var asked = _time.GetUtcNow();
        _scheduler.Request([App]);
        Quiet(1.9);
        await GraceAsync();
        _refresher.Started.ShouldBeEmpty();

        Quiet(0.1);
        await StartedAsync(App);
        _refresher.Complete(App, Succeeded);

        await StatesAsync(App, BindingSyncState.Stale, BindingSyncState.Updating, BindingSyncState.Ready);
        // Started by the clock reaching two quiet seconds, not before.
        Statuses().Single(s => s.Status.State == BindingSyncState.Updating).At.ShouldBe(asked + BindingSyncScheduler.Quiet);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Saves_in_a_row_make_one_refresh_per_project_one_at_a_time()
    {
        _scheduler.Request([App]);
        Quiet(1);
        _scheduler.Request([App, Web]);
        Quiet();

        await StartedAsync(App);
        _refresher.Complete(App, Succeeded);
        await StartedAsync(App, Web);

        // Web started only once App was done.
        var order = Statuses().Select(s => (s.Status.Target, s.Status.State)).ToList();
        order.IndexOf((App, BindingSyncState.Ready)).ShouldBeLessThan(order.IndexOf((Web, BindingSyncState.Updating)));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_project_asked_for_again_while_refreshing_is_cancelled_and_refreshed_anew()
    {
        _scheduler.Request([App]);
        Quiet();
        await StartedAsync(App);
        var first = _refresher.TokenOf(App);

        _scheduler.Request([App]);

        first.IsCancellationRequested.ShouldBeTrue();
        Quiet();
        await StartedAsync(App, App);
    }

    // A successful build may concern another project, or may have been Clean.
    [Fact, Trait("Category", "Unit")]
    public async Task A_build_holds_refreshes_back_then_verifies_all_pending_projects()
    {
        _scheduler.Request([App]);
        _scheduler.BuildStarted();
        Quiet();
        _scheduler.Request([Web]);
        Quiet();
        await GraceAsync();
        _refresher.Started.ShouldBeEmpty();

        _scheduler.BuildFinished();

        await StartedAsync(App);
        StatesOf(App).ShouldNotContain(BindingSyncState.Ready);
        _refresher.Complete(App, Succeeded);
        await StartedAsync(App, Web);
        StatesOf(App).Last().ShouldBe(BindingSyncState.Ready);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_build_stops_a_running_refresh()
    {
        _scheduler.Request([App]);
        Quiet();
        await StartedAsync(App);
        var running = _refresher.TokenOf(App);

        _scheduler.BuildStarted();
        running.IsCancellationRequested.ShouldBeTrue();
        _scheduler.BuildFinished();
        Quiet();

        await StartedAsync(App, App);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_or_cancelled_build_does_not_claim_the_bindings_are_current()
    {
        _scheduler.Request([App]);
        _scheduler.BuildStarted();
        Quiet();

        _scheduler.BuildFinished(succeeded: false);

        StatesOf(App).ShouldBe([BindingSyncState.Stale, BindingSyncState.Failed]);
        await GraceAsync();
        _refresher.Started.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failure_is_reported_with_its_reason_and_the_next_save_tries_again()
    {
        _scheduler.Request([App]);
        Quiet();
        await StartedAsync(App);
        _refresher.Complete(App, new BindingRefreshResult(false, "Main.daml(3,1): error EQD0001: parse error"));
        await StatesAsync(App, BindingSyncState.Stale, BindingSyncState.Updating, BindingSyncState.Failed);

        Statuses().Last().Status.Message.ShouldBe("Main.daml(3,1): error EQD0001: parse error");
        _scheduler.Request([App]);
        Quiet();
        await StartedAsync(App, App);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_refresher_exception_reports_failure_and_does_not_stop_other_projects()
    {
        _scheduler.Request([App, Web]);
        Quiet();
        await StartedAsync(App);

        _refresher.Fail(App, new InvalidOperationException("The project was unloaded."));

        await StartedAsync(App, Web);
        StatesOf(App).Last().ShouldBe(BindingSyncState.Failed);
        _refresher.Complete(Web, Succeeded);
        await Wait.UntilAsync(() => StatesOf(Web).LastOrDefault() == BindingSyncState.Ready, "Web to be ready");
    }

    // Another solution, or projects added or removed: nothing queued still applies.
    [Fact, Trait("Category", "Unit")]
    public async Task Clearing_cancels_the_running_refresh_and_forgets_the_rest()
    {
        _scheduler.Request([App, Web]);
        Quiet();
        await StartedAsync(App);
        var running = _refresher.TokenOf(App);

        _scheduler.Clear();
        running.IsCancellationRequested.ShouldBeTrue();
        await StatesAsync(App, BindingSyncState.Stale, BindingSyncState.Updating, BindingSyncState.Cancelled);
        Quiet();
        await GraceAsync();

        _refresher.Started.ShouldBe([App]);
    }

    // Cancelled from the Task Status Center: the run ends with a word for it, so its entry stops spinning.
    [Fact, Trait("Category", "Unit")]
    public async Task A_refresh_stopped_with_nothing_to_follow_it_says_it_was_cancelled()
    {
        _scheduler.Request([App]);
        Quiet();
        await StartedAsync(App);

        _scheduler.Clear();

        await StatesAsync(App, BindingSyncState.Stale, BindingSyncState.Updating, BindingSyncState.Cancelled);
    }

    // Asked for again, or taken over by a build: another run or the build follows, so no cancellation is reported.
    [Fact, Trait("Category", "Unit")]
    public async Task A_refresh_stopped_to_run_again_or_for_a_build_is_not_reported_as_cancelled()
    {
        _scheduler.Request([App]);
        Quiet();
        await StartedAsync(App);
        _scheduler.Request([App]);
        Quiet();
        await StartedAsync(App, App);
        _scheduler.BuildStarted();
        _scheduler.BuildFinished();
        Quiet();
        await StartedAsync(App, App, App);

        StatesOf(App).ShouldNotContain(BindingSyncState.Cancelled);
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

        public void Fail(BindingRefreshTarget target, Exception exception)
        {
            lock (_gate)
            {
                _runs.Last(r => r.Target == target).Result.TrySetException(exception);
            }
        }
    }
}
