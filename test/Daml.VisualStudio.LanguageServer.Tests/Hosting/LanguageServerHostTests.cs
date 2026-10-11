using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.FileSystem;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Supervision;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Hosting;

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
        using var host = Host(new FakeProcessRunner(), new InMemoryFileSystem(), _ => null);
        var workspace = Single(@"C:\repo") with
        {
            Mode = multiPackage ? ServerMode.MultiIde : ServerMode.Ide,
            MultiPackageManifestPath = multiPackage ? @"C:\repo\multi-package.yaml" : null,
        };
        host.SetWorkspace(workspace, new LanguageServerOptions());
        var requests = 0;
        host.Supervisor.RestartRequested += (_, _) => requests++;

        host.OnFileChanged(changed);

        requests.ShouldBe(restarts ? 1 : 0);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_manifest_appearing_while_there_is_no_workspace_asks_for_a_new_start()
    {
        using var host = Host(new FakeProcessRunner(), new InMemoryFileSystem(), _ => null);
        host.SetWorkspace(null, new LanguageServerOptions());
        var reasons = new List<string>();
        host.Supervisor.RestartRequested += (_, e) => reasons.AddRange(e.Reasons);

        host.OnFileChanged(@"C:\repo\daml.yaml");
        host.OnFileChanged(@"C:\repo\daml\Main.daml");

        reasons.ShouldHaveSingleItem().ShouldContain("daml.yaml");
    }

    // The fake language server stands in for dpm: it ignores its arguments, fails the version
    // and help commands (no stdin), and then serves LSP when started as the server.
    [Fact, Trait("Category", "Unit")]
    public async Task A_session_is_ready_once_initialize_is_answered_and_the_rpc_channel_belongs_to_it()
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
        host.SessionReady += (_, e) => ready.TrySetResult(e.SessionId);

        var connection = (await host.ActivateAsync(CancellationToken.None)).ShouldNotBeNull();
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(connection.ToServer, connection.FromServer, new JsonMessageFormatter()));
        rpc.StartListening();
        host.AttachRpc(rpc);
        await rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = 1, capabilities = new { } });

        (await Task.WhenAny(ready.Task, Task.Delay(Generous))).ShouldBe(ready.Task);
        (await ready.Task).ShouldBe(connection.SessionId);
        host.Supervisor.State.ShouldBe(LanguageServerState.Ready);
        host.Channel.TryGet(out var attached, out var sessionId).ShouldBeTrue();
        attached.ShouldBeSameAs(rpc);
        sessionId.ShouldBe(connection.SessionId);
        host.LastResolution.ShouldNotBeNull().ContextSdkVersion.ShouldBe("3.6.0");

        await rpc.InvokeAsync<object?>("shutdown");
        await rpc.NotifyAsync("exit");
        await host.StopAsync(CancellationToken.None);
        host.Channel.TryGet(out _, out _).ShouldBeFalse();
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
