using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.Core.Sdk;
using Equilibrium.Daml.VisualStudio.Core.Server;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;
using Equilibrium.Daml.VisualStudio.LanguageServer.Supervision;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Hosting;

public sealed class LanguageServerHostTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly List<LanguageServerProblem> _problems = new();

    private static WorkspaceContext Single(string root) => new()
    {
        WorkspaceRoot = root,
        ServerWorkingDirectory = root,
        Mode = ServerMode.Ide,
        PackageRoot = root,
        Packages = [root],
        Version = 1,
    };

    private LanguageServerHost Host(IProcessRunner runner, IDamlFileSystem files, Func<string, string?> environment, TimeProvider? time = null, RestartPolicy? policy = null)
    {
        var resolver = new SdkResolver(runner, new AssistantLocator(files, environment), new YamlManifestReader(files));
        var host = new LanguageServerHost(runner, resolver, NullDamlLogger.Instance, time ?? new FakeTimeProvider(), policy);
        host.ProblemRaised += (_, e) => _problems.Add(e.Problem);
        return host;
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_a_daml_yaml_nothing_starts_and_the_user_is_told()
    {
        var runner = new FakeProcessRunner();
        using var host = Host(runner, new InMemoryFileSystem(), _ => null);
        host.SetWorkspace(null, new LanguageServerOptions());

        (await host.ActivateAsync(CancellationToken.None)).ShouldBeNull();

        _problems.ShouldHaveSingleItem().Kind.ShouldBe(LanguageServerProblemKind.NoWorkspace);
        runner.Started.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_missing_tool_is_reported_with_its_instructions()
    {
        var files = new InMemoryFileSystem();
        files.AddFile(@"C:\repo\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");
        using var host = Host(new FakeProcessRunner(), files, _ => null);
        host.SetWorkspace(Single(@"C:\repo"), new LanguageServerOptions());

        (await host.ActivateAsync(CancellationToken.None)).ShouldBeNull();

        var problem = _problems.ShouldHaveSingleItem();
        problem.Kind.ShouldBe(LanguageServerProblemKind.ToolMissing);
        problem.Message.ShouldContain("dpm");
    }

    // A server that writes something that isn't LSP framing, or a frame past the limit, is broken
    // as surely as one that exited: the session ends and counts as a crash, so it is restarted.
    // net48 has no Memory overloads of the stream methods.
#pragma warning disable CA1835
    [Fact, Trait("Category", "Unit")]
    public async Task A_frame_the_server_cant_be_trusted_with_ends_the_session_as_a_crash()
    {
        var files = new InMemoryFileSystem();
        files.AddFile(@"C:\repo\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");
        files.AddFile(@"C:\dpm\dpm.exe", "");
        var runner = new FakeProcessRunner();
        runner.On(@"C:\dpm\dpm.exe", ["versions", "-o", "json"], """[{"version":"3.6.0","installed":true,"active":true}]""");
        runner.On(@"C:\dpm\dpm.exe", ["damlc", "--help"], new ProcessResult(1, "", "no help today"));
        using var host = Host(runner, files, _ => null, policy: new RestartPolicy([TimeSpan.FromMinutes(1)], maxCrashes: 5, window: TimeSpan.FromMinutes(3)));
        host.SetWorkspace(Single(@"C:\repo"), new LanguageServerOptions { DpmPath = @"C:\dpm\dpm.exe" });
        var connection = (await host.ActivateAsync(CancellationToken.None)).ShouldNotBeNull();
        var output = runner.Started[0].StandardOutput;
        var oversized = System.Text.Encoding.ASCII.GetBytes("Content-Length: 999999999\r\n\r\n");
        await output.WriteAsync(oversized, 0, oversized.Length);
        output.Position = 0;

        // The framing error itself, or the end of the stream it caused, depending on which side of
        // the adapter sees it first: Visual Studio's side of the connection fails either way.
        await Should.ThrowAsync<System.IO.IOException>(() => connection.FromServer.ReadAsync(new byte[16], 0, 16));

        await Wait.UntilAsync(() => host.Supervisor.State == LanguageServerState.Crashed, "the session to end as a crash");
        runner.Started[0].Disposed.ShouldBeTrue();
    }
#pragma warning restore CA1835

    [Fact, Trait("Category", "Unit")]
    public async Task An_open_circuit_returns_no_connection_and_offers_a_restart()
    {
        var files = new InMemoryFileSystem();
        files.AddFile(@"C:\repo\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");
        files.AddFile(@"C:\dpm\dpm.exe", "");
        var runner = new FakeProcessRunner();
        runner.On(@"C:\dpm\dpm.exe", ["versions", "-o", "json"], """[{"version":"3.6.0","installed":true,"active":true}]""");
        runner.On(@"C:\dpm\dpm.exe", ["damlc", "--help"], new ProcessResult(1, "", "no help today"));
        using var host = Host(runner, files, _ => null, policy: new RestartPolicy([TimeSpan.Zero], maxCrashes: 0, window: TimeSpan.FromMinutes(3)));
        host.SetWorkspace(Single(@"C:\repo"), new LanguageServerOptions { DpmPath = @"C:\dpm\dpm.exe" });

        (await host.ActivateAsync(CancellationToken.None)).ShouldNotBeNull();
        runner.Started[0].Exit(1);
        var deadline = DateTime.UtcNow + Generous;
        while (host.Supervisor.State != LanguageServerState.Faulted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        (await host.ActivateAsync(CancellationToken.None)).ShouldBeNull();
        _problems.ShouldContain(p => p.Kind == LanguageServerProblemKind.CircuitOpen);
        runner.Started.Count.ShouldBe(1);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\daml.yaml", false, true)]
    [InlineData(@"C:\repo\daml.yaml", true, false)]
    [InlineData(@"C:\repo\multi-package.yaml", true, true)]
    [InlineData(@"C:\repo\daml\Main.daml", false, false)]
    [InlineData(@"C:\elsewhere\daml.yaml", false, false)]
    public void Manifest_changes_ask_for_a_restart_when_the_server_wont_notice_them(string changed, bool multiPackage, bool restarts)
    {
        var time = new FakeTimeProvider();
        using var host = Host(new FakeProcessRunner(), new InMemoryFileSystem(), _ => null, time);
        var workspace = Single(@"C:\repo") with
        {
            Mode = multiPackage ? ServerMode.MultiIde : ServerMode.Ide,
            MultiPackageManifestPath = multiPackage ? @"C:\repo\multi-package.yaml" : null,
        };
        host.SetWorkspace(workspace, new LanguageServerOptions());
        var requests = 0;
        host.Supervisor.RestartRequested += (_, _) => requests++;

        host.OnFileChanged(changed);
        time.Advance(TimeSpan.FromSeconds(1));

        requests.ShouldBe(restarts ? 1 : 0);
    }

    // One save is several change events; they add up to one restart, asked for once the
    // changes have been quiet for a moment.
    [Fact, Trait("Category", "Unit")]
    public void The_change_events_of_one_save_ask_for_one_restart()
    {
        var time = new FakeTimeProvider();
        using var host = Host(new FakeProcessRunner(), new InMemoryFileSystem(), _ => null, time);
        host.SetWorkspace(Single(@"C:\repo"), new LanguageServerOptions());
        var requests = 0;
        host.Supervisor.RestartRequested += (_, _) => requests++;

        host.OnFileChanged(@"C:\repo\daml.yaml");
        time.Advance(TimeSpan.FromMilliseconds(200));
        host.OnFileChanged(@"C:\repo\daml.yaml");
        host.OnFileChanged(@"C:\repo\daml.yaml");
        time.Advance(TimeSpan.FromMilliseconds(400));
        requests.ShouldBe(0, "the last change was 400 ms ago");
        time.Advance(TimeSpan.FromMilliseconds(200));

        requests.ShouldBe(1);
    }

    // The package database, and the manifests the C# generator writes for itself in the work folder
    // of a generation root on every run, which a solution's C# projects keep under the workspace.
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\.daml\package-database\2.3\lib-1.0.0-abc\daml.yaml")]
    [InlineData(@"C:\repo\App\obj\Debug\net10.0\Daml\.daml-work\9de80d6cfe50bada.run\daml.yaml")]
    [InlineData(@"C:\repo\App\obj\Debug\net10.0\Daml\.daml-work\9de80d6cfe50bada.run\multi-package.yaml")]
    public void Changes_under_the_build_output_are_ignored(string path)
    {
        var time = new FakeTimeProvider();
        using var host = Host(new FakeProcessRunner(), new InMemoryFileSystem(), _ => null, time);
        host.SetWorkspace(Single(@"C:\repo"), new LanguageServerOptions());

        host.OnFileChanged(path);
        time.Advance(TimeSpan.FromSeconds(1));

        host.Supervisor.PendingRestartReasons.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_watcher_that_lost_events_asks_for_a_restart_at_once()
    {
        using var host = Host(new FakeProcessRunner(), new InMemoryFileSystem(), _ => null);
        host.SetWorkspace(Single(@"C:\repo"), new LanguageServerOptions());

        host.OnWatcherOverflow();

        host.Supervisor.PendingRestartReasons.ShouldHaveSingleItem().ShouldContain("lost events");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_program_that_cant_be_created_is_reported_as_a_start_problem()
    {
        var files = new InMemoryFileSystem();
        files.AddFile(@"C:\repo\daml.yaml", "sdk-version: 3.6.0\nname: app\nversion: 1.0.0\nsource: daml\n");
        files.AddFile(@"C:\dpm\dpm.exe", "");
        var runner = new FakeProcessRunner();
        runner.On(@"C:\dpm\dpm.exe", ["versions", "-o", "json"], """[{"version":"3.6.0","installed":true,"active":true}]""");
        runner.On(@"C:\dpm\dpm.exe", ["damlc", "--help"], new ProcessResult(1, "", "no help today"));
        runner.FailStarts(new System.IO.FileNotFoundException("dpm.exe vanished"));
        using var host = Host(runner, files, _ => null);
        host.SetWorkspace(Single(@"C:\repo"), new LanguageServerOptions { DpmPath = @"C:\dpm\dpm.exe" });

        (await host.ActivateAsync(CancellationToken.None)).ShouldBeNull();

        var problem = _problems.ShouldHaveSingleItem();
        problem.Kind.ShouldBe(LanguageServerProblemKind.CannotStart);
        problem.Message.ShouldContain("vanished");
        host.Supervisor.State.ShouldBe(LanguageServerState.Stopped);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_manifest_appearing_while_there_is_no_workspace_asks_for_a_new_start()
    {
        var time = new FakeTimeProvider();
        using var host = Host(new FakeProcessRunner(), new InMemoryFileSystem(), _ => null, time);
        host.SetWorkspace(null, new LanguageServerOptions());
        var reasons = new List<string>();
        host.Supervisor.RestartRequested += (_, e) => reasons.AddRange(e.Reasons);

        host.OnFileChanged(@"C:\repo\daml.yaml");
        host.OnFileChanged(@"C:\repo\daml\Main.daml");
        time.Advance(TimeSpan.FromSeconds(1));

        reasons.ShouldHaveSingleItem().ShouldContain("daml.yaml");
    }

    // The fake language server stands in for dpm: it ignores its arguments, fails the version
    // and help commands (no stdin), and then serves LSP when started as the server.
    [Fact, Trait("Category", "Process")]
    public async Task A_session_is_ready_once_the_client_has_said_initialized_and_the_rpc_channel_belongs_to_it()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        var files = new PhysicalFileSystem();
        var package = Repository.Sample("single-package");
        var environment = server.Environment;
        using var host = new LanguageServerHost(
            new EnvironmentRunner(new ProcessRunner(), environment),
            new SdkResolver(new EnvironmentRunner(new ProcessRunner(), environment), new AssistantLocator(files, _ => null), new YamlManifestReader(files)),
            NullDamlLogger.Instance,
            TimeProvider.System);
        host.SetWorkspace(Single(package), new LanguageServerOptions { DpmPath = server.ExecutablePath });
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyEvents = 0;
        host.SessionReady += (_, e) => { Interlocked.Increment(ref readyEvents); ready.TrySetResult(e.SessionId); };
        var readiness = new List<bool>();
        host.ReadinessChanged += (_, _) => { lock (readiness) { readiness.Add(host.IsReady); } };

        var connection = (await host.ActivateAsync(CancellationToken.None)).ShouldNotBeNull();
        host.IsReady.ShouldBeFalse("nothing has answered initialize yet");
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(connection.ToServer, connection.FromServer, new JsonMessageFormatter()));
        rpc.StartListening();
        host.AttachRpc(rpc);
        await rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, capabilities = new { } });
        host.Supervisor.State.ShouldBe(LanguageServerState.Initializing, "Visual Studio hasn't finished the handshake yet");
        host.IsReady.ShouldBeFalse();
        await rpc.NotifyWithParameterObjectAsync("initialized", new { });

        (await Task.WhenAny(ready.Task, Task.Delay(Generous))).ShouldBe(ready.Task);
        (await ready.Task).ShouldBe(connection.SessionId);
        host.Supervisor.State.ShouldBe(LanguageServerState.Ready);
        host.Channel.TryGet(out var attached, out var sessionId).ShouldBeTrue();
        attached.ShouldBeSameAs(rpc);
        sessionId.ShouldBe(connection.SessionId);
        host.LastResolution.ShouldNotBeNull().ContextSdkVersion.ShouldBe("3.6.0");
        host.IsReady.ShouldBeTrue();
        host.AttachRpc(rpc);
        readyEvents.ShouldBe(1, "attaching the same connection again must not resubscribe session features");

        await rpc.InvokeAsync<object?>("shutdown");
        await rpc.NotifyAsync("exit");
        await host.StopAsync(CancellationToken.None);
        host.Channel.TryGet(out _, out _).ShouldBeFalse();
        host.IsReady.ShouldBeFalse();
        lock (readiness)
        {
            readiness.ShouldBe([true, false], "each change is announced once");
        }
    }

    [Theory, Trait("Category", "Process")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_ready_server_is_asked_whether_it_is_alive_and_restarted_when_it_hangs(bool hangs)
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario { HangOnKeepAlive = hangs });
        var files = new PhysicalFileSystem();
        var environment = server.Environment;
        var time = new FakeTimeProvider();
        using var host = new LanguageServerHost(
            new EnvironmentRunner(new ProcessRunner(), environment),
            new SdkResolver(new EnvironmentRunner(new ProcessRunner(), environment), new AssistantLocator(files, _ => null), new YamlManifestReader(files)),
            NullDamlLogger.Instance,
            time);
        host.ProblemRaised += (_, e) => _problems.Add(e.Problem);
        var restarts = new List<string>();
        host.Supervisor.RestartRequested += (_, e) => restarts.AddRange(e.Reasons);
        host.SetWorkspace(Single(Repository.Sample("single-package")), new LanguageServerOptions { DpmPath = server.ExecutablePath });
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionReady += (_, e) => ready.TrySetResult(e.SessionId);
        var connection = (await host.ActivateAsync(CancellationToken.None)).ShouldNotBeNull();
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(connection.ToServer, connection.FromServer, new JsonMessageFormatter()));
        rpc.StartListening();
        host.AttachRpc(rpc);
        await rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, capabilities = new { } });
        await rpc.NotifyWithParameterObjectAsync("initialized", new { });
        (await Task.WhenAny(ready.Task, Task.Delay(Generous))).ShouldBe(ready.Task);

        time.Advance(TimeSpan.FromSeconds(60));
        (await EventuallyAsync(() => server.ReadReceivedMethods().Contains("daml/keepAlive"))).ShouldBeTrue("no keep-alive request reached the server");
        if (!hangs)
        {
            // The answer has to be in before the clock reaches the deadline.
            (await EventuallyAsync(() => host.KeepAlive.Answered == 1 && host.KeepAlive.IsSettled)).ShouldBeTrue();
        }
        time.Advance(TimeSpan.FromSeconds(120));
        (await EventuallyAsync(() => host.KeepAlive.IsSettled)).ShouldBeTrue();

        if (hangs)
        {
            (await EventuallyAsync(() => _problems.Count > 0)).ShouldBeTrue();
            _problems.ShouldHaveSingleItem().Kind.ShouldBe(LanguageServerProblemKind.Unresponsive);
            restarts.ShouldBe(["The language server stopped responding"]);
            host.IsUnresponsive.ShouldBeTrue();
        }
        else
        {
            _problems.ShouldBeEmpty();
            restarts.ShouldBeEmpty();
            (await EventuallyAsync(() => server.ReadReceivedMethods().Count(m => m == "daml/keepAlive") == 2)).ShouldBeTrue("answered, so asked again a minute later");
            (await EventuallyAsync(() => host.KeepAlive.Answered == 2 && host.KeepAlive.IsSettled)).ShouldBeTrue();

            host.KeepAliveSettings = new KeepAliveSettings { Enabled = false };
            (await EventuallyAsync(() => host.KeepAlive.IsSettled)).ShouldBeTrue("the ended loop has stopped");
            time.Advance(TimeSpan.FromMinutes(10));
            server.ReadReceivedMethods().Count(m => m == "daml/keepAlive").ShouldBe(2, "switched off");

            host.KeepAliveSettings = new KeepAliveSettings();
            time.Advance(TimeSpan.FromSeconds(60));
            (await EventuallyAsync(() => server.ReadReceivedMethods().Count(m => m == "daml/keepAlive") == 3)).ShouldBeTrue("switched on again for the ready session");
        }
        await host.StopAsync(CancellationToken.None, immediately: true);
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

    // Adds the fake server's scenario variables to everything started, as VS's environment would.
    private sealed class EnvironmentRunner(IProcessRunner inner, IReadOnlyDictionary<string, string> environment) : IProcessRunner
    {
        private ProcessSpec With(ProcessSpec spec) =>
            spec with { Environment = environment.ToDictionary(p => p.Key, p => (string?)p.Value) };

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) => inner.RunAsync(With(spec), cancellationToken);

        public IRunningProcess Start(ProcessSpec spec) => inner.Start(With(spec));
    }
}
