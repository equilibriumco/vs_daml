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
        _transitions.Select(t => t.To).ShouldBe([LanguageServerState.Starting, LanguageServerState.Running, LanguageServerState.Initializing]);
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
        await Task.Delay(100);

        second.ShouldNotBeNull().Id.ShouldBe(2);
        _supervisor.State.ShouldBe(LanguageServerState.Initializing);
        _transitions.ShouldNotContain(t => t.To == LanguageServerState.Crashed);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Concurrent_starts_run_one_after_the_other()
    {
        var gate = new TaskCompletionSource<ProcessSpec>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var supervisor = new LanguageServerSupervisor(_runner, _ => Interlocked.Increment(ref calls) == 1 ? gate.Task : Task.FromResult(Command), NullDamlLogger.Instance, _time);

        var first = supervisor.StartAsync(CancellationToken.None);
        var second = supervisor.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        calls.ShouldBe(1);
        gate.SetResult(Command);

        (await first).ShouldNotBeNull().Id.ShouldBe(1);
        (await second).ShouldNotBeNull().Id.ShouldBe(2);
        _runner.Started[0].Disposed.ShouldBeTrue();
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

    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_start_counts_as_a_crash_and_is_reported()
    {
        _runner.FailStarts(new FileNotFoundException("dpm.cmd is gone"));

        await Should.ThrowAsync<FileNotFoundException>(() => StartAsync());

        _supervisor.State.ShouldBe(LanguageServerState.Crashed);
        _runner.FailStarts(null);
        var restart = StartAsync();
        _supervisor.State.ShouldBe(LanguageServerState.Restarting);
        _time.Advance(TimeSpan.FromSeconds(1));
        (await restart).ShouldNotBeNull();
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
}
