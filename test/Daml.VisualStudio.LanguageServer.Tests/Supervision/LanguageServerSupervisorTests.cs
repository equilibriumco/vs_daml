using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.LanguageServer.Supervision;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Supervision;

public sealed class LanguageServerSupervisorTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private static readonly ProcessSpec Command = new(@"C:\dpm\bin\dpm.cmd", ["damlc", "multi-ide"]) { WorkingDirectory = @"C:\repo" };

    private readonly FakeProcessRunner _runner = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly List<(LanguageServerState From, LanguageServerState To, int SessionId)> _transitions = new();
    private readonly List<IReadOnlyList<string>> _restartRequests = new();
    private readonly LanguageServerSupervisor _supervisor;

    public LanguageServerSupervisorTests()
    {
        _supervisor = new LanguageServerSupervisor(_runner, _ => Task.FromResult(Command), NullDamlLogger.Instance, _time);
        _supervisor.StateChanged += (_, e) =>
        {
            lock (_transitions)
            {
                _transitions.Add((e.OldState, e.NewState, e.SessionId));
            }
        };
        _supervisor.RestartRequested += (_, e) => _restartRequests.Add(e.Reasons);
    }

    public void Dispose() => _supervisor.Dispose();

    private Task<LanguageServerSession?> StartAsync(CancellationToken cancellationToken = default) => _supervisor.StartAsync(cancellationToken);

    // Exits are noticed on a thread-pool continuation; wait for the state they lead to.
    private async Task CrashAsync(int processIndex, LanguageServerState expected = LanguageServerState.Crashed)
    {
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, LanguageServerStateChangedEventArgs e)
        {
            if (e.NewState == expected)
            {
                reached.TrySetResult(true);
            }
        }
        _supervisor.StateChanged += OnChanged;
        try
        {
            _runner.Started[processIndex].Exit(1);
            (await Task.WhenAny(reached.Task, Task.Delay(Generous))).ShouldBe(reached.Task, $"never reached {expected}");
        }
        finally
        {
            _supervisor.StateChanged -= OnChanged;
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Starting_hands_out_the_server_streams_and_waits_for_initialize()
    {
        var session = (await StartAsync()).ShouldNotBeNull();

        session.Id.ShouldBe(1);
        var process = _runner.Started.ShouldHaveSingleItem();
        process.Spec.ShouldBe(Command);
        session.Input.ShouldBeSameAs(process.StandardInput);
        session.Output.ShouldBeSameAs(process.StandardOutput);
        _supervisor.State.ShouldBe(LanguageServerState.Initializing);
        _transitions.Select(t => t.To).ShouldBe([LanguageServerState.Starting, LanguageServerState.Initializing]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Ready_is_reached_only_for_the_current_session()
    {
        await StartAsync();

        _supervisor.MarkReady(sessionId: 7);
        _supervisor.State.ShouldBe(LanguageServerState.Initializing);
        _supervisor.MarkReady(sessionId: 1);
        _supervisor.State.ShouldBe(LanguageServerState.Ready);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task After_a_crash_the_next_start_waits_out_the_backoff()
    {
        await StartAsync();
        await CrashAsync(0);

        var restart = StartAsync();

        _supervisor.State.ShouldBe(LanguageServerState.Restarting);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        restart.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        (await restart).ShouldNotBeNull().Id.ShouldBe(2);
        _runner.Started.Count.ShouldBe(2);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Backoff_grows_with_each_crash()
    {
        var waited = new List<double>();
        await StartAsync();
        for (var crash = 0; crash < 5; crash++)
        {
            await CrashAsync(crash);
            var restart = StartAsync();
            var before = _time.GetUtcNow();
            while (!restart.IsCompleted)
            {
                _time.Advance(TimeSpan.FromMilliseconds(500));
                await Task.Yield();
                await Task.Delay(1);
            }
            (await restart).ShouldNotBeNull();
            waited.Add((_time.GetUtcNow() - before).TotalSeconds);
        }

        waited.Zip(new[] { 1.0, 2, 4, 8, 16 }, (actual, expected) => actual >= expected && actual <= expected + 0.5).ShouldAllBe(ok => ok);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Too_many_crashes_open_the_circuit_and_starts_return_null()
    {
        var supervisor = new LanguageServerSupervisor(_runner, _ => Task.FromResult(Command), NullDamlLogger.Instance, _time,
            new RestartPolicy([TimeSpan.Zero], maxCrashes: 2, window: TimeSpan.FromMinutes(3)));
        var faulted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.StateChanged += (_, e) =>
        {
            if (e.NewState == LanguageServerState.Faulted)
            {
                faulted.TrySetResult(true);
            }
        };

        for (var i = 0; i < 3; i++)
        {
            (await supervisor.StartAsync(CancellationToken.None)).ShouldNotBeNull();
            _runner.Started[i].Exit(1);
            await Task.Delay(50);
        }
        (await Task.WhenAny(faulted.Task, Task.Delay(Generous))).ShouldBe(faulted.Task);

        (await supervisor.StartAsync(CancellationToken.None)).ShouldBeNull();
        _runner.Started.Count.ShouldBe(3);
        supervisor.State.ShouldBe(LanguageServerState.Faulted);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Resetting_the_circuit_asks_for_a_restart_that_starts_at_once()
    {
        var supervisor = new LanguageServerSupervisor(_runner, _ => Task.FromResult(Command), NullDamlLogger.Instance, _time,
            new RestartPolicy([TimeSpan.FromSeconds(30)], maxCrashes: 0, window: TimeSpan.FromMinutes(3)));
        var reasons = new List<IReadOnlyList<string>>();
        supervisor.RestartRequested += (_, e) => reasons.Add(e.Reasons);
        await supervisor.StartAsync(CancellationToken.None);
        _runner.Started[0].Exit(1);
        while (supervisor.State != LanguageServerState.Faulted)
        {
            await Task.Delay(10);
        }

        supervisor.ResetCircuit();

        supervisor.State.ShouldBe(LanguageServerState.Stopped);
        reasons.ShouldHaveSingleItem().ShouldBe(["Restart requested by the user"]);
        var session = await supervisor.StartAsync(CancellationToken.None);
        session.ShouldNotBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_exit_of_an_older_session_does_not_touch_the_current_one()
    {
        await StartAsync();
        var second = await StartAsync();

        _runner.Started[0].Disposed.ShouldBeTrue();
        _runner.Started[0].Exit(1);
        // The exit is looked at on a continuation; wait for it to have been, then judge.
        var deadline = DateTime.UtcNow + Generous;
        while (_supervisor.ExitsHandled < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        _supervisor.ExitsHandled.ShouldBeGreaterThanOrEqualTo(1);

        second.ShouldNotBeNull().Id.ShouldBe(2);
        _supervisor.State.ShouldBe(LanguageServerState.Initializing);
        _transitions.ShouldNotContain(t => t.To == LanguageServerState.Crashed);
    }

    // Resolving the tool (dpm versions, damlc --help) doesn't hold the other start up; the
    // process creation and the hand-over still happen one at a time, the earlier session giving
    // way to the later one.
    [Fact, Trait("Category", "Unit")]
    public async Task Concurrent_starts_resolve_at_once_but_create_sessions_one_after_the_other()
    {
        var gate = new TaskCompletionSource<ProcessSpec>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var supervisor = new LanguageServerSupervisor(_runner, _ => Interlocked.Increment(ref calls) == 1 ? gate.Task : Task.FromResult(Command), NullDamlLogger.Instance, _time);

        var first = supervisor.StartAsync(CancellationToken.None);
        var second = supervisor.StartAsync(CancellationToken.None);
        calls.ShouldBe(2, "both resolved before either waited for the other");
        gate.SetResult(Command);

        var sessions = new[] { (await first).ShouldNotBeNull(), (await second).ShouldNotBeNull() };
        sessions.Select(s => s.Id).OrderBy(id => id).ShouldBe([1, 2]);
        _runner.Started.Count.ShouldBe(2);
        _runner.Started[0].Disposed.ShouldBeTrue("the earlier session was superseded");
        _runner.Started[1].Disposed.ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Restart_requests_are_merged_until_the_next_start()
    {
        await StartAsync();

        _supervisor.RequestRestart("daml.yaml changed");
        _supervisor.RequestRestart("options changed");
        _supervisor.RequestRestart("daml.yaml changed");

        _restartRequests.ShouldHaveSingleItem().ShouldBe(["daml.yaml changed"]);
        _supervisor.PendingRestartReasons.ShouldBe(["daml.yaml changed", "options changed"]);

        await StartAsync();
        _supervisor.PendingRestartReasons.ShouldBeEmpty();
        _supervisor.RequestRestart("options changed");
        _restartRequests.Count.ShouldBe(2);
    }

    // A start that fails (dpm path wrong) still answered the restart request; the same request
    // made after the user fixes the path must start the server again, not join a stale one.
    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_start_still_clears_the_restart_reasons()
    {
        var failing = new LanguageServerSupervisor(_runner, _ => throw new InvalidOperationException("dpm path doesn't exist"), NullDamlLogger.Instance, _time);
        var requests = new List<IReadOnlyList<string>>();
        failing.RestartRequested += (_, e) => requests.Add(e.Reasons);
        failing.RequestRestart("Options changed");

        await Should.ThrowAsync<InvalidOperationException>(() => failing.StartAsync(CancellationToken.None));

        failing.PendingRestartReasons.ShouldBeEmpty();
        failing.RequestRestart("Options changed");
        requests.Count.ShouldBe(2, "the second request is a new restart");
        failing.Dispose();
    }

    // A program that can't be created is the user's setup, not a crash: no backoff to wait out
    // once they have fixed it.
    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_start_is_not_a_crash_and_the_next_start_is_immediate()
    {
        _runner.FailStarts(new FileNotFoundException("dpm.cmd is gone"));

        await Should.ThrowAsync<FileNotFoundException>(() => StartAsync());

        _supervisor.State.ShouldBe(LanguageServerState.Stopped);
        _transitions.ShouldNotContain(t => t.To == LanguageServerState.Crashed);
        _runner.FailStarts(null);
        (await StartAsync()).ShouldNotBeNull();
        _transitions.ShouldNotContain(t => t.To == LanguageServerState.Restarting);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_stop_the_caller_gives_up_on_still_ends_the_session()
    {
        await StartAsync();
        // The grace period runs on the fake clock, so only the caller giving up can end the wait.
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => _supervisor.StopAsync(giveUp.Token));
        _runner.Started[0].Disposed.ShouldBeTrue("the process tree is ended either way");
        _supervisor.State.ShouldBe(LanguageServerState.Stopped);
    }

    // A connection the adapter had to close (a malformed or oversized message) leaves the
    // process alive; counting it as a crash is what keeps a broken server from restarting in a
    // tight loop.
    [Fact, Trait("Category", "Unit")]
    public async Task A_connection_failure_ends_the_session_and_counts_as_a_crash()
    {
        var session = (await StartAsync()).ShouldNotBeNull();

        _supervisor.ReportConnectionFailure(session.Id, new IOException("a frame of 33 MB"));

        _supervisor.State.ShouldBe(LanguageServerState.Crashed);
        _runner.Started[0].Disposed.ShouldBeTrue();
        var restart = StartAsync();
        _supervisor.State.ShouldBe(LanguageServerState.Restarting, "the backoff applies");
        _time.Advance(TimeSpan.FromSeconds(1));
        (await restart).ShouldNotBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_connection_failure_of_an_older_session_changes_nothing()
    {
        var session = (await StartAsync()).ShouldNotBeNull();

        _supervisor.ReportConnectionFailure(session.Id - 1, new IOException("stale"));

        _supervisor.State.ShouldBe(LanguageServerState.Initializing);
        _runner.Started[0].Disposed.ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_initialize_reply_that_arrives_after_the_crash_doesnt_make_the_session_ready()
    {
        var session = (await StartAsync()).ShouldNotBeNull();
        await CrashAsync(0);

        _supervisor.MarkReady(session.Id);

        _supervisor.State.ShouldBe(LanguageServerState.Crashed);
        _transitions.ShouldNotContain(t => t.To == LanguageServerState.Ready);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_disposed_supervisor_is_stopped_and_refuses_to_start()
    {
        await StartAsync();

        _supervisor.Dispose();

        _supervisor.State.ShouldBe(LanguageServerState.Stopped);
        _runner.Started[0].Disposed.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() => StartAsync());
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Cancelling_during_the_backoff_starts_nothing()
    {
        await StartAsync();
        await CrashAsync(0);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // The 1 s backoff is on the fake clock, so only the cancellation can end the wait.
        await Should.ThrowAsync<OperationCanceledException>(() => StartAsync(cancel.Token));
        _runner.Started.Count.ShouldBe(1);
        _supervisor.State.ShouldBe(LanguageServerState.Crashed);
    }

    // The lock around the crash record exists for this: a process that leaves while the stop
    // is waiting for it is a stop that worked, not a crash.
    [Fact, Trait("Category", "Unit")]
    public async Task A_process_that_leaves_during_the_stop_is_stopped_not_crashed()
    {
        await StartAsync();
        var stop = _supervisor.StopAsync(CancellationToken.None);
        _supervisor.State.ShouldBe(LanguageServerState.Stopping);

        _runner.Started[0].Exit(1);
        await stop;

        _supervisor.State.ShouldBe(LanguageServerState.Stopped);
        _transitions.ShouldNotContain(t => t.To == LanguageServerState.Crashed);
        var restart = StartAsync();
        _transitions.ShouldNotContain(t => t.To == LanguageServerState.Restarting, "no backoff after a clean stop");
        (await restart).ShouldNotBeNull();
    }
}
