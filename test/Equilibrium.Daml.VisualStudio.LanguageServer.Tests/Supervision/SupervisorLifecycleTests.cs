using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Supervision;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Supervision;

// Stopping, the initialize watchdog and stderr handling: against a scripted process with a fake
// clock, and against the fake language server executable in its normal, crashing and hanging modes.
public sealed class SupervisorLifecycleTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private static readonly ProcessSpec Command = new(@"C:\dpm\bin\dpm.cmd", ["damlc", "ide"]);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger _logger = new();

    private LanguageServerSupervisor Scripted(FakeProcessRunner runner) =>
        new(runner, _ => Task.FromResult(Command), _logger, _time);

    private static async Task<bool> ReachesAsync(LanguageServerSupervisor supervisor, LanguageServerState state)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (supervisor.State != state && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        return supervisor.State == state;
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Stopping_ends_the_session_first_and_waits_for_the_server_to_leave()
    {
        var runner = new FakeProcessRunner();
        using var supervisor = Scripted(runner);
        var session = (await supervisor.StartAsync(CancellationToken.None)).ShouldNotBeNull();

        var stop = supervisor.StopAsync(CancellationToken.None);

        session.Ending.IsCancellationRequested.ShouldBeTrue();
        supervisor.State.ShouldBe(LanguageServerState.Stopping);
        runner.Started[0].Exit(0);
        await stop;
        supervisor.State.ShouldBe(LanguageServerState.Stopped);
        runner.Started[0].Disposed.ShouldBeTrue();
        _logger.Entries.ShouldNotContain(e => e.Message.Contains("exited with code"));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_server_that_does_not_leave_in_time_is_killed()
    {
        var runner = new FakeProcessRunner();
        using var supervisor = Scripted(runner);
        await supervisor.StartAsync(CancellationToken.None);

        var stop = supervisor.StopAsync(CancellationToken.None);
        supervisor.State.ShouldBe(LanguageServerState.Stopping, "the process hasn't left and the clock hasn't moved");
        stop.IsCompleted.ShouldBeFalse();
        await RunOutTheGracePeriodAsync(stop);

        runner.Started[0].Disposed.ShouldBeTrue();
        supervisor.State.ShouldBe(LanguageServerState.Stopped);
        _logger.Entries.ShouldContain(e => e.Level == DamlLogLevel.Warning && e.Message.Contains("killing it"));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Stopping_without_a_session_just_reports_stopped()
    {
        using var supervisor = Scripted(new FakeProcessRunner());

        await supervisor.StopAsync(CancellationToken.None);

        supervisor.State.ShouldBe(LanguageServerState.Stopped);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_server_that_never_answers_initialize_is_killed_and_counts_as_a_crash()
    {
        var runner = new FakeProcessRunner();
        using var supervisor = Scripted(runner);
        var session = (await supervisor.StartAsync(CancellationToken.None)).ShouldNotBeNull();

        _time.Advance(TimeSpan.FromMinutes(2));

        (await ReachesAsync(supervisor, LanguageServerState.Crashed)).ShouldBeTrue();
        runner.Started[0].Killed.ShouldBeTrue();
        // The crash is recorded first and the session ended right after, outside the lock;
        // whoever is told about the crash is told after both.
        SpinWait.SpinUntil(() => session.Ending.IsCancellationRequested, TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_server_that_became_ready_is_left_alone_by_the_watchdog()
    {
        var runner = new FakeProcessRunner();
        using var supervisor = Scripted(runner);
        var session = (await supervisor.StartAsync(CancellationToken.None)).ShouldNotBeNull();

        supervisor.MarkReady(session.Id);
        _time.Advance(TimeSpan.FromMinutes(5));
        // The watchdog resumes on the thread pool: give a kill that shouldn't come the chance to.
        await Task.Delay(100);

        runner.Started[0].Killed.ShouldBeFalse();
        supervisor.State.ShouldBe(LanguageServerState.Ready);
    }

    [Fact, Trait("Category", "Process")]
    public async Task A_normal_server_initializes_and_stops_without_a_crash()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        using var supervisor = Real(server, out var states);
        var session = (await supervisor.StartAsync(CancellationToken.None)).ShouldNotBeNull();
        using var rpc = Connect(session);

        var reply = await rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, capabilities = new { } });
        supervisor.MarkReady(session.Id);
        await rpc.InvokeAsync<object?>("shutdown");
        await rpc.NotifyAsync("exit");
        await supervisor.StopAsync(CancellationToken.None);

        reply["serverInfo"]!["name"]!.ToString().ShouldBe("fake-daml-language-server");
        supervisor.State.ShouldBe(LanguageServerState.Stopped);
        states.ShouldBe([LanguageServerState.Starting, LanguageServerState.Initializing,
            LanguageServerState.Ready, LanguageServerState.Stopping, LanguageServerState.Stopped]);
    }

    [Fact, Trait("Category", "Process")]
    public async Task A_server_exiting_right_after_start_is_a_crash()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario { ExitCodeAtStart = 1 });
        using var supervisor = Real(server, out _);

        await supervisor.StartAsync(CancellationToken.None);

        (await ReachesAsync(supervisor, LanguageServerState.Crashed)).ShouldBeTrue();
        _logger.Entries.ShouldContain(e => e.Message.Contains("exited with code 1"));
    }

    [Fact, Trait("Category", "Process")]
    public async Task A_hanging_server_is_killed_by_the_initialize_watchdog()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario { HangOnInitialize = true });
        using var supervisor = Real(server, out _, new SupervisorSettings { InitializeTimeout = TimeSpan.FromSeconds(1) }, TimeProvider.System);
        var session = (await supervisor.StartAsync(CancellationToken.None)).ShouldNotBeNull();
        using var rpc = Connect(session);

        var initialize = rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, capabilities = new { } });

        (await ReachesAsync(supervisor, LanguageServerState.Crashed)).ShouldBeTrue();
        // The pending request belongs to the JSON-RPC connection, not to any UI context.
#pragma warning disable VSTHRD003
        await Should.ThrowAsync<Exception>(() => initialize);
#pragma warning restore VSTHRD003
        _logger.Entries.ShouldContain(e => e.Level == DamlLogLevel.Error && e.Message.Contains("didn't answer initialize"));
    }

    [Fact, Trait("Category", "Process")]
    public async Task Stderr_is_kept_raised_and_logged_within_the_rate_limit()
    {
        var scenario = new FakeLanguageServerScenario { HangOnInitialize = true };
        scenario.StderrLines.AddRange(Enumerable.Range(1, 120).Select(i => $"line {i}"));
        using var server = FakeLanguageServer.Create(scenario);
        using var supervisor = Real(server, out _, new SupervisorSettings { StderrLinesPerSecond = 10, RetainedStderrLines = 100 });
        var raised = 0;
        supervisor.StdErrLine += (_, _) => Interlocked.Increment(ref raised);

        await supervisor.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow + Generous;
        while (Volatile.Read(ref raised) < 120 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        // The server is stuck in initialize and never leaves, so the grace period has to run out.
        await RunOutTheGracePeriodAsync(supervisor.StopAsync(CancellationToken.None));

        raised.ShouldBe(120);
        supervisor.RecentStderr.Count.ShouldBe(100);
        supervisor.RecentStderr[^1].ShouldBe("line 120");
        // The clock doesn't move, so it's one window: ten lines shown, the rest summarised.
        _logger.Entries.Count(e => e.Message.StartsWith("line ", StringComparison.Ordinal)).ShouldBe(10);
        (await WaitForEntryAsync(e => e.Message.StartsWith("110 stderr line(s)", StringComparison.Ordinal))).ShouldBeTrue();
    }

    // The grace timer is registered on the fake clock somewhere after the stop started; moving
    // the clock a second at a time, yielding in between, reaches it whenever that was.
    // The stop isn't tied to any synchronization context, so awaiting it here can't deadlock.
#pragma warning disable VSTHRD003
    private async Task RunOutTheGracePeriodAsync(Task stop)
    {
        for (var seconds = 0; seconds < 30 && !stop.IsCompleted; seconds++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        await stop;
    }
#pragma warning restore VSTHRD003

    private LanguageServerSupervisor Real(FakeLanguageServer server, out List<LanguageServerState> states, SupervisorSettings? settings = null, TimeProvider? time = null)
    {
        var command = new ProcessSpec(server.ExecutablePath, []) { Environment = server.Environment.ToDictionary(p => p.Key, p => (string?)p.Value) };
        var supervisor = new LanguageServerSupervisor(new ProcessRunner(), _ => Task.FromResult(command), _logger, time ?? _time, settings: settings);
        var seen = new List<LanguageServerState>();
        supervisor.StateChanged += (_, e) =>
        {
            lock (seen)
            {
                seen.Add(e.NewState);
            }
        };
        states = seen;
        return supervisor;
    }

    private static JsonRpc Connect(LanguageServerSession session)
    {
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(session.Input, session.Output, new JsonMessageFormatter()));
        rpc.StartListening();
        return rpc;
    }

    private async Task<bool> WaitForEntryAsync(Func<LogEntry, bool> match)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (!_logger.Entries.Any(match) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        return _logger.Entries.Any(match);
    }

    // The limit is per second: once the clock has moved on, lines are shown again and what the
    // earlier window dropped is summarised on its own.
    [Fact, Trait("Category", "Process")]
    public async Task The_stderr_limit_starts_over_with_each_second()
    {
        var scenario = new FakeLanguageServerScenario();
        scenario.StderrLines.AddRange(Enumerable.Range(1, 60).Select(i => $"early {i}"));
        scenario.StderrLinesAfterInitialized.AddRange(Enumerable.Range(1, 60).Select(i => $"late {i}"));
        using var server = FakeLanguageServer.Create(scenario);
        using var supervisor = Real(server, out _, new SupervisorSettings { StderrLinesPerSecond = 10 });
        var raised = 0;
        supervisor.StdErrLine += (_, _) => Interlocked.Increment(ref raised);

        var session = (await supervisor.StartAsync(CancellationToken.None)).ShouldNotBeNull();
        (await EventuallyAsync(() => Volatile.Read(ref raised) >= 60)).ShouldBeTrue("the early lines");
        _time.Advance(TimeSpan.FromSeconds(1));
        using var rpc = Connect(session);
        await rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, capabilities = new { } });
        await rpc.NotifyWithParameterObjectAsync("initialized", new { });
        (await EventuallyAsync(() => Volatile.Read(ref raised) >= 120)).ShouldBeTrue("the late lines");
        await rpc.InvokeAsync<object?>("shutdown");
        await rpc.NotifyAsync("exit");
        await RunOutTheGracePeriodAsync(supervisor.StopAsync(CancellationToken.None));

        _logger.Entries.Count(e => e.Message.StartsWith("early ", StringComparison.Ordinal)).ShouldBe(10);
        _logger.Entries.Count(e => e.Message.StartsWith("late ", StringComparison.Ordinal)).ShouldBe(10);
        (await EventuallyAsync(() => _logger.Entries.Count(e => e.Message.StartsWith("50 stderr line(s)", StringComparison.Ordinal)) == 2)).ShouldBeTrue("one summary per window");
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }
            await Task.Delay(20);
        }
        return true;
    }
}
