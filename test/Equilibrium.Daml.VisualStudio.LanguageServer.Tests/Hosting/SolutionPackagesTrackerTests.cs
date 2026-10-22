using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Hosting;

// Projects come and go in a solution, a burst of them while it loads; the server is restarted once
// the burst is over, and only when the set of Daml packages it serves has changed.
public sealed class SolutionPackagesTrackerTests : IDisposable
{
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SolutionPackagesTracker _tracker;
    private IReadOnlyList<string> _manifests = [];
    private WorkspaceContext? _workspace;
    private int _restarts;

    public SolutionPackagesTrackerTests()
    {
        _files.AddFile(@"C:\sln\A\daml.yaml", "sdk-version: 3.6.0\nname: a\nversion: 1.0.0\nsource: daml\n");
        _files.AddFile(@"C:\sln\B\daml.yaml", "sdk-version: 3.6.0\nname: b\nversion: 1.0.0\nsource: daml\n");
        _tracker = new SolutionPackagesTracker(
            new WorkspaceDiscovery(_files, new YamlManifestReader(_files)),
            () => Task.FromResult(_manifests),
            () => _workspace,
            () =>
            {
                _restarts++;
                return Task.CompletedTask;
            },
            _time);
    }

    public void Dispose() => _tracker.Dispose();

    private static WorkspaceContext Serving(params string[] solutionPackages) => new()
    {
        WorkspaceRoot = @"C:\sln",
        ServerWorkingDirectory = @"C:\sln",
        Mode = ServerMode.MultiIde,
        SolutionPackages = solutionPackages,
        Version = 1,
    };

    private async Task ChangedAndQuietAsync()
    {
        var change = _tracker.OnProjectsChangedAsync();
        _time.Advance(SolutionPackagesTracker.Quiet);
        await change;
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_new_Daml_project_restarts_the_server()
    {
        _workspace = Serving(@"C:\sln\A");
        _manifests = [@"C:\sln\A\daml.yaml", @"C:\sln\B\daml.yaml"];

        await ChangedAndQuietAsync();

        _restarts.ShouldBe(1);
    }

    // C# projects, or a Daml project whose daml.yaml isn't there yet, don't change what is served.
    [Fact, Trait("Category", "Unit")]
    public async Task The_same_Daml_packages_leave_the_server_alone()
    {
        _workspace = Serving(@"C:\sln\A", @"C:\sln\B");
        _manifests = [@"C:\sln\B\daml.yaml", @"C:\sln\A\daml.yaml", @"C:\sln\New\daml.yaml"];

        await ChangedAndQuietAsync();

        _restarts.ShouldBe(0);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_burst_of_changes_restarts_once_after_it()
    {
        _workspace = Serving(@"C:\sln\A");
        _manifests = [@"C:\sln\A\daml.yaml", @"C:\sln\B\daml.yaml"];

        var first = _tracker.OnProjectsChangedAsync();
        _time.Advance(TimeSpan.FromTicks(SolutionPackagesTracker.Quiet.Ticks / 2));
        var second = _tracker.OnProjectsChangedAsync();
        _time.Advance(SolutionPackagesTracker.Quiet);
        await Task.WhenAll(first, second);

        _restarts.ShouldBe(1);
    }

    // A solution without Daml gets its first Daml project: the server starts; with none, nothing happens.
    [Theory, Trait("Category", "Unit")]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Without_a_server_a_Daml_project_starts_one(bool withDaml, int restarts)
    {
        _manifests = withDaml ? [@"C:\sln\A\daml.yaml"] : [];

        await ChangedAndQuietAsync();

        _restarts.ShouldBe(restarts);
    }
}
