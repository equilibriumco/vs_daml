using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Supervision;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Hosting;

public sealed class ClientRestartSequenceTests : IDisposable
{
    private const string Dpm = @"C:\dpm\dpm.exe";

    private readonly FakeProcessRunner _runner = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LanguageServerHost _host;
    private readonly List<string> _steps = new();
    private readonly List<LanguageServerProblemKind> _problems = new();
    private WorkspaceContext? _discovered;

    public ClientRestartSequenceTests()
    {
        var files = new InMemoryFileSystem();
        files.AddFile(@"C:\repo\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");
        files.AddFile(Dpm, "");
        _runner.On(Dpm, ["versions", "-o", "json"], """[{"version":"3.6.0","installed":true,"active":true}]""");
        _runner.On(Dpm, ["damlc", "--help"], new Core.Processes.ProcessResult(1, "", "no help"));
        var resolver = new SdkResolver(_runner, new AssistantLocator(files, _ => null), new YamlManifestReader(files));
        _host = new LanguageServerHost(_runner, resolver, NullDamlLogger.Instance, _time);
        _host.ProblemRaised += (_, e) => _problems.Add(e.Problem.Kind);
        _discovered = Workspace();
    }

    public void Dispose() => _host.Dispose();

    private static WorkspaceContext Workspace() => new()
    {
        WorkspaceRoot = @"C:\repo",
        ServerWorkingDirectory = @"C:\repo",
        Mode = ServerMode.Ide,
        PackageRoot = @"C:\repo",
        Packages = [@"C:\repo"],
        Version = 1,
    };

    private ClientRestartSequence Sequence() => new(
        _host,
        _ =>
        {
            _steps.Add("discover");
            return Task.FromResult<(WorkspaceContext?, LanguageServerOptions)>((_discovered, new LanguageServerOptions { DpmPath = Dpm }));
        },
        () =>
        {
            _steps.Add(_runner.Started.Count > 0 && _runner.Started[^1].Disposed ? "stop (server already gone)" : "stop");
            // VS sends shutdown and exit on StopAsync, and a healthy server leaves.
            if (_runner.Started.Count > 0)
            {
                _runner.Started[^1].Exit(0);
            }
            return Task.CompletedTask;
        },
        async () =>
        {
            _steps.Add("start");
            await _host.ActivateAsync(CancellationToken.None);
        });

    [Fact, Trait("Category", "Unit")]
    public async Task A_ready_server_is_stopped_through_the_client_and_started_again()
    {
        var sequence = Sequence();
        await sequence.StartAsync(CancellationToken.None);
        _host.Supervisor.MarkReady(1);

        await sequence.RestartAsync(CancellationToken.None);

        _steps.ShouldBe(["discover", "start", "discover", "stop", "start"]);
        _runner.Started.Count.ShouldBe(2);
        _runner.Started[0].Disposed.ShouldBeTrue();
        _runner.Started[0].Killed.ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_server_that_is_not_ready_is_killed_before_the_client_is_stopped()
    {
        var sequence = Sequence();
        await sequence.StartAsync(CancellationToken.None);

        await sequence.RestartAsync(CancellationToken.None);

        _steps.ShouldBe(["discover", "start", "discover", "stop (server already gone)", "start"]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_a_workspace_the_server_is_stopped_and_not_started_again()
    {
        var sequence = Sequence();
        await sequence.StartAsync(CancellationToken.None);
        _host.Supervisor.MarkReady(1);
        _discovered = null;

        await sequence.RestartAsync(CancellationToken.None);

        _steps.ShouldBe(["discover", "start", "discover", "stop"]);
        _problems.ShouldContain(LanguageServerProblemKind.NoWorkspace);
        _host.Supervisor.State.ShouldBe(LanguageServerState.Stopped);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_workspace_that_appears_later_is_simply_started()
    {
        _discovered = null;
        var sequence = Sequence();
        await sequence.StartAsync(CancellationToken.None);
        _discovered = Workspace();

        await sequence.RestartAsync(CancellationToken.None);

        _steps.ShouldBe(["discover", "discover", "start"]);
        _runner.Started.Count.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Restart_requests_from_the_supervisor_run_the_sequence()
    {
        var sequence = Sequence();
        await sequence.StartAsync(CancellationToken.None);
        _host.Supervisor.MarkReady(1);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sequence.RestartCompleted += (_, _) => finished.TrySetResult(true);

        _host.Supervisor.RequestRestart("options changed");

        (await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(finished.Task);
        _steps.ShouldBe(["discover", "start", "discover", "stop", "start"]);
    }

    // A hung server wouldn't answer VS's shutdown either, so it goes first.
    [Fact, Trait("Category", "Unit")]
    public async Task A_ready_server_that_stopped_answering_is_killed_before_the_client_is_stopped()
    {
        var sequence = Sequence();
        await sequence.StartAsync(CancellationToken.None);
        _host.Supervisor.MarkReady(1);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sequence.RestartCompleted += (_, _) => finished.TrySetResult(true);

        _host.OnUnresponsive(1);

        (await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(finished.Task);
        _steps.ShouldBe(["discover", "start", "discover", "stop (server already gone)", "start"]);
        _problems.ShouldBe([LanguageServerProblemKind.Unresponsive]);
        _host.IsUnresponsive.ShouldBeFalse("the new session starts with a clean slate");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_report_about_an_older_session_changes_nothing()
    {
        var sequence = Sequence();
        await sequence.StartAsync(CancellationToken.None);
        _host.Supervisor.MarkReady(1);

        _host.OnUnresponsive(0);

        _problems.ShouldBeEmpty();
        _host.Supervisor.PendingRestartReasons.ShouldBeEmpty();
        _host.IsUnresponsive.ShouldBeFalse();
    }
}
