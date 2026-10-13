using System;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.LanguageServer.Supervision;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Supervision;

// The test deliberately pauses resolution on a task it owns; no VS synchronization context is involved.
#pragma warning disable VSTHRD003
public class StartupRaceTests
{
    private static readonly ProcessSpec Command = new(@"C:\dpm.exe", []);

    [Fact, Trait("Category", "Unit")]
    public async Task Stop_invalidates_a_pending_resolution_even_if_the_resolver_ignores_cancellation()
    {
        var resolved = new TaskCompletionSource<ProcessSpec>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeProcessRunner();
        using var supervisor = new LanguageServerSupervisor(runner, _ => resolved.Task, NullDamlLogger.Instance);
        var start = supervisor.StartAsync(CancellationToken.None);

        await supervisor.StopAsync(CancellationToken.None, TimeSpan.Zero);
        resolved.SetResult(Command);

        await Should.ThrowAsync<OperationCanceledException>(() => start);
        runner.Started.ShouldBeEmpty();
        supervisor.State.ShouldBe(LanguageServerState.Stopped);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Dispose_during_process_creation_reclaims_the_new_process()
    {
        LanguageServerSupervisor? supervisor = null;
        var runner = new StartRunner(_ => supervisor!.Dispose());
        using (supervisor = new LanguageServerSupervisor(runner, _ => Task.FromResult(Command), NullDamlLogger.Instance))
        {
            await Should.ThrowAsync<OperationCanceledException>(() => supervisor.StartAsync(CancellationToken.None));
            runner.Process!.Disposed.ShouldBeTrue();
            supervisor.State.ShouldBe(LanguageServerState.Stopped);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_process_that_exits_before_the_handover_counts_as_a_crash_without_resurrecting_initializing()
    {
        var runner = new StartRunner(process => process.Exit(1));
        using var supervisor = new LanguageServerSupervisor(runner, _ => Task.FromResult(Command), NullDamlLogger.Instance);

        var session = await supervisor.StartAsync(CancellationToken.None);

        session.ShouldBeNull();
        supervisor.State.ShouldBe(LanguageServerState.Crashed);
        runner.Process!.Disposed.ShouldBeTrue();
    }

    private sealed class StartRunner(Action<FakeRunningProcess> onStart) : IProcessRunner
    {
        public FakeRunningProcess? Process { get; private set; }

        public IRunningProcess Start(ProcessSpec spec)
        {
            Process = new FakeRunningProcess(spec);
            onStart(Process);
            return Process;
        }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
