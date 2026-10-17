using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.MSBuild;

// Daml projects as users write them, <Project Sdk="Daml.Sdk/…">, with NuGet resolving the SDK
// from a local feed, in MSBuild.exe of Visual Studio 2026 and in dotnet msbuild, with a fake dpm.
public sealed class DamlSdkOnHostsTests : IDisposable
{
    private const string Manifest = "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n";

    private readonly MSBuildSandbox _sandbox = new();
    private readonly FakeDpm _dpm = FakeDpm.Create(new FakeDpmScenario());

    public void Dispose()
    {
        _dpm.Dispose();
        _sandbox.Dispose();
    }

    private static string HostPath(MSBuildHost host) =>
        RequiredTools.Require(host == MSBuildHost.MSBuildExe ? ExternalTool.MSBuild : ExternalTool.Dotnet);

    private string Package => Path.Combine(_sandbox.Root, "contracts");

    private string DamlProject(string manifest = Manifest)
    {
        _sandbox.UseSdkFeed();
        _sandbox.Write(@"contracts\daml.yaml", manifest);
        _sandbox.Write(@"contracts\daml\Main.daml", "module Main where\n");
        return _sandbox.Write(@"contracts\Contracts.msbuildproj", $"<Project Sdk=\"Daml.Sdk/{SdkFeed.Version}\">\n</Project>\n");
    }

    private void Scenario(params FakeDpmCommand[] commands)
    {
        var scenario = new FakeDpmScenario();
        scenario.Commands.AddRange(commands);
        var version = new FakeDpmCommand { Stdout = "version: 1.0.22\nbuild: b65a815\n" };
        version.Args.Add("--version");
        scenario.Commands.Add(version);
        _dpm.SetScenario(scenario);
    }

    private static FakeDpmCommand BuildCommand(string stderr, int exitCode = 0)
    {
        var command = new FakeDpmCommand { Stderr = stderr, ExitCode = exitCode };
        command.Args.Add("build");
        return command;
    }

    private static FakeDpmCommand CreatesTheDar()
    {
        var created = BuildCommand("Compiling contracts to a DAR.\nCreated .daml\\dist\\contracts-1.2.0.dar\n");
        created.WriteFiles[@".daml\dist\contracts-1.2.0.dar"] = "dar";
        return created;
    }

    private MSBuildRun Build(MSBuildHost host, string project, string? target = null, params (string Name, string Value)[] properties)
    {
        var all = properties.ToDictionary(p => p.Name, p => p.Value);
        all["DamlDpmPath"] = _dpm.ExecutablePath;
        return _sandbox.Build(host, HostPath(host), project, _dpm.Environment, all, target);
    }

    private List<FakeDpmCall> Calls(string verb) => _dpm.ReadCalls().Where(c => c.Args.Count == 1 && c.Args[0] == verb).ToList();

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_Daml_project_builds_its_DAR_through_the_SDK_on_each_host(MSBuildHost host)
    {
        Scenario(CreatesTheDar());

        var run = Build(host, DamlProject());

        run.ExitCode.ShouldBe(0, run.Log);
        run.Log.ShouldContain($@"Contracts -> {Package}\.daml\dist\contracts-1.2.0.dar");
        Calls("build").ShouldHaveSingleItem().WorkingDirectory.ShouldBe(Package);
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void Daml_errors_come_out_where_they_are(MSBuildHost host)
    {
        Scenario(BuildCommand(File.ReadAllText(Path.Combine(Repository.Root, @"test\fixtures\build-output\build-broken.stderr.txt")), exitCode: 1));

        var run = Build(host, DamlProject());

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain($@"{Package}\daml\TypeError.daml(5,9,5,20): typecheck error DAML0001: • Couldn't match expected type ‘Int’ with actual type ‘Text’");
        run.Log.ShouldNotContain("MSB6006");
    }

    // Visual Studio restores and runs design-time builds all the time; neither may start a tool.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void Restore_and_design_time_builds_run_no_tool(MSBuildHost host)
    {
        Scenario(CreatesTheDar());
        var project = DamlProject();

        var restore = Build(host, project, target: "Restore");
        var designTime = Build(host, project, null, ("DesignTimeBuild", "true"));
        var dar = Build(host, project, target: "GetDamlDar", ("DesignTimeBuild", "true"));

        restore.ExitCode.ShouldBe(0, restore.Log);
        designTime.ExitCode.ShouldBe(0, designTime.Log);
        dar.ExitCode.ShouldBe(0, dar.Log);
        _dpm.ReadCalls().ShouldBeEmpty();
    }

    // The ProjectReference protocol: the C# project builds the Daml project first, then asks it for its DAR.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_referencing_project_gets_the_built_DAR_from_GetDamlDar(MSBuildHost host)
    {
        Scenario(CreatesTheDar());
        DamlProject();
        var app = _sandbox.Write(@"App\App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\contracts\Contracts.msbuildproj" ReferenceOutputAssembly="false" />
              </ItemGroup>
              <Target Name="ShowDamlDar" AfterTargets="ResolveProjectReferences">
                <MSBuild Projects="@(ProjectReference)" Targets="GetDamlDar">
                  <Output TaskParameter="TargetOutputs" ItemName="_DamlDar" />
                </MSBuild>
                <Message Importance="high" Text="DAR: @(_DamlDar) exists=%(_DamlDar.Exists) package=%(_DamlDar.DamlPackageName)-%(_DamlDar.DamlPackageVersion)" />
              </Target>
            </Project>
            """);

        var run = Build(host, app);

        run.ExitCode.ShouldBe(0, run.Log);
        run.Log.ShouldContain($@"DAR: {Package}\.daml\dist\contracts-1.2.0.dar exists=True package=contracts-1.2.0");
        Calls("build").ShouldHaveSingleItem();
    }
}
