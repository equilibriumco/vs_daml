using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Daml.Build.Tests.MSBuild;

// The dotnet new templates as users run them: the templates package of this test run, the projects
// it creates restored from the local feed and built by MSBuild.exe of Visual Studio 2026 and by
// dotnet msbuild, with a fake dpm, and with the real dpm, generator and compiler.
public sealed class DamlTemplatesTests : IDisposable
{
    private readonly MSBuildSandbox _sandbox = new(shortRoot: true);
    private readonly FakeDpm _dpm = FakeDpm.Create(new FakeDpmScenario());
    private readonly ITestOutputHelper _output;

    public DamlTemplatesTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        _dpm.Dispose();
        _sandbox.Dispose();
    }

    private static string HostPath(MSBuildHost host) =>
        RequiredTools.Require(host == MSBuildHost.MSBuildExe ? ExternalTool.MSBuild : ExternalTool.Dotnet);

    private async Task NewAsync(params string[] arguments)
    {
        _sandbox.UseSdkFeed();
        var created = await TemplateHive.NewAsync(_sandbox.Root, arguments);
        created.ExitCode.ShouldBe(0, created.Stdout + created.Stderr);
    }

    private string Folder(string relativePath) => Path.Combine(_sandbox.Root, relativePath);

    private void Scenario(params FakeDpmCommand[] commands)
    {
        var scenario = new FakeDpmScenario();
        scenario.Commands.AddRange(commands);
        var version = new FakeDpmCommand { Stdout = "version: 1.0.22\nbuild: b65a815\n" };
        version.Args.Add("--version");
        scenario.Commands.Add(version);
        _dpm.SetScenario(scenario);
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public async Task A_new_contracts_project_builds_the_contracts_before_their_tests(MSBuildHost host)
    {
        await NewAsync("daml-contracts", "-n", "Acme.Contracts", "-o", "Acme.Contracts");
        // One answer for both packages: each writes both DARs, and each project looks for its own.
        var build = new FakeDpmCommand();
        build.Args.Add("build");
        build.WriteFiles[@".daml\dist\acme-contracts-0.1.0.dar"] = "dar";
        build.WriteFiles[@".daml\dist\acme-contracts-tests-0.1.0.dar"] = "dar";
        Scenario(build);

        var run = _sandbox.Build(host, HostPath(host), @"Acme.Contracts\Acme.Contracts.Tests\Acme.Contracts.Tests.msbuildproj",
            _dpm.Environment, new Dictionary<string, string> { ["DamlDpmPath"] = _dpm.ExecutablePath });

        run.ExitCode.ShouldBe(0, run.Log);
        var contracts = Folder(@"Acme.Contracts\Acme.Contracts");
        var tests = Folder(@"Acme.Contracts\Acme.Contracts.Tests");
        run.Log.ShouldContain($@"Acme.Contracts -> {contracts}\.daml\dist\acme-contracts-0.1.0.dar");
        run.Log.ShouldContain($@"Acme.Contracts.Tests -> {tests}\.daml\dist\acme-contracts-tests-0.1.0.dar");
        _dpm.ReadCalls().Where(c => c.Args.SequenceEqual(["build"])).Select(c => c.WorkingDirectory).ShouldBe([contracts, tests]);
    }

    // Everything the solution's projects need comes from the feed: the packages of this release, and
    // the runtime the generated code is for.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public async Task A_new_app_solution_restores_each_of_its_projects(MSBuildHost host)
    {
        await NewAsync("daml-dotnet-app", "-n", "Demo.App", "-o", "Demo.App");

        var run = _sandbox.Build(host, HostPath(host), @"Demo.App\Demo.App.slnx", _dpm.Environment, target: "Restore", restore: false);

        run.ExitCode.ShouldBe(0, run.Log);
        foreach (var project in new[] { "Demo.App", "Demo.App.Contracts", "Demo.App.Contracts.Tests" })
        {
            File.Exists(Folder($@"Demo.App\{project}\obj\project.assets.json")).ShouldBeTrue(project);
        }
        _dpm.ReadCalls().ShouldBeEmpty();
    }

    // The whole path a new user takes: dpm builds both packages, the pinned generator turns the contracts
    // into C#, the app compiles against them and runs, and the Daml Script tests pass, and fail once broken.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public async Task A_new_app_solution_builds_runs_and_tests_its_contracts(MSBuildHost host)
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        var dotnet = RequiredTools.Require(ExternalTool.Dotnet);
        var hostPath = HostPath(host);
        await NewAsync("daml-dotnet-app", "-n", "Demo.App", "-o", "Demo.App");
        var properties = new Dictionary<string, string>
        {
            ["DamlDpmPath"] = dpm,
            ["DamlJavaHome"] = Path.GetDirectoryName(Path.GetDirectoryName(java))!,
            // Installs the generator once on a machine that hasn't got it yet.
            ["DamlAllowToolInstall"] = "true",
        };
        const string Tests = @"Demo.App\Demo.App.Contracts.Tests\Demo.App.Contracts.Tests.msbuildproj";

        var build = _sandbox.Build(host, hostPath, @"Demo.App\Demo.App.slnx", properties: properties);
        build.ExitCode.ShouldBe(0, build.Log);
        build.Log.ShouldContain("Generated C# for");
        var app = await new ProcessRunner().RunAsync(
            new ProcessSpec(dotnet, [Folder(@"Demo.App\Demo.App\bin\Debug\net10.0\Demo.App.dll")]) { Timeout = TimeSpan.FromMinutes(1) },
            CancellationToken.None);
        app.ExitCode.ShouldBe(0, app.Stdout + app.Stderr);
        app.Stdout.Trim().ShouldBe("Gold from Daml package demo-app-contracts 0.1.0");

        var passing = _sandbox.Build(host, hostPath, Tests, properties: properties, target: "DamlTest");
        passing.ExitCode.ShouldBe(0, passing.Log);

        var script = Folder(@"Demo.App\Demo.App.Contracts.Tests\daml\Test.daml");
        File.WriteAllText(script, File.ReadAllText(script).Replace("found.owner === bob", "found.owner === alice"));
        var failing = _sandbox.Build(host, hostPath, Tests, properties: properties, target: "DamlTest");
        failing.ExitCode.ShouldNotBe(0, failing.Log);
        failing.Log.ShouldContain("Script error DAML0001");
        _output.WriteLine($"{host}: build {build.Elapsed.TotalSeconds:F1} s, tests {passing.Elapsed.TotalSeconds:F1} s.");
    }
}
