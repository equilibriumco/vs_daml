using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.MSBuild;

// The tasks as MSBuild loads them: from a folder of their own, in MSBuild.exe of Visual Studio
// 2026 (.NET Framework) and in dotnet msbuild (.NET 10), with no Visual Studio around.
public sealed class DamlTasksOnHostsTests : IDisposable
{
    private const string PackageProject = """
        <Project>
          <UsingTask TaskName="Daml.Build.Tasks.ReadDamlManifest" AssemblyFile="$(DamlTasksAssembly)" />
          <UsingTask TaskName="Daml.Build.Tasks.DamlBuildTask" AssemblyFile="$(DamlTasksAssembly)" />
          <Target Name="Build">
            <ReadDamlManifest ManifestPath="$(MSBuildProjectDirectory)\daml.yaml">
              <Output TaskParameter="DarPath" PropertyName="_DamlDar" />
            </ReadDamlManifest>
            <DamlBuildTask ManifestPath="$(MSBuildProjectDirectory)\daml.yaml" DarPath="$(_DamlDar)" DpmPath="$(DamlDpmPath)" ProcessTimeoutSeconds="$(DamlProcessTimeoutSeconds)">
              <Output TaskParameter="CreatedDar" PropertyName="_DamlCreatedDar" />
            </DamlBuildTask>
            <Message Importance="high" Text="DAR: $(_DamlCreatedDar)" />
            <Message Importance="high" Text="Host: $(MSBuildRuntimeType) $(MSBuildExtensionsPath)" />
          </Target>
        </Project>
        """;

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

    private string Package(string folder, string manifest = Manifest)
    {
        _sandbox.Write(Path.Combine(folder, "daml.yaml"), manifest);
        _sandbox.Write(Path.Combine(folder, "daml", "Main.daml"), "module Main where\n");
        return _sandbox.Write(Path.Combine(folder, "Contracts.proj"), PackageProject);
    }

    private void Scenario(params FakeDpmCommand[] commands)
    {
        var scenario = new FakeDpmScenario();
        scenario.Commands.AddRange(commands);
        var version = new FakeDpmCommand { Stdout = "version: 1.0.22\nbuild: b65a815\nbuildDate: b65a815\n" };
        version.Args.Add("--version");
        scenario.Commands.Add(version);
        _dpm.SetScenario(scenario);
    }

    private static FakeDpmCommand BuildCommand(string stderr, int exitCode = 0, int delay = 0)
    {
        var command = new FakeDpmCommand { Stderr = stderr, ExitCode = exitCode, DelayMilliseconds = delay };
        command.Args.Add("build");
        return command;
    }

    private MSBuildRun BuildWithFake(MSBuildHost host, string project, params (string Name, string Value)[] properties)
    {
        var all = properties.ToDictionary(p => p.Name, p => p.Value);
        all["DamlDpmPath"] = _dpm.ExecutablePath;
        return _sandbox.Build(host, HostPath(host), project, _dpm.Environment, all);
    }

    private List<FakeDpmCall> Calls(string verb) => _dpm.ReadCalls().Where(c => c.Args.Count == 1 && c.Args[0] == verb).ToList();

    // What a package ships next to its tasks: no MSBuild or framework assembly, which the host
    // provides and a second copy would clash with.
    [Fact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void The_tasks_folder_holds_the_tasks_and_their_own_dependencies_only() =>
        Directory.GetFiles(_sandbox.Tasks).Select(Path.GetFileName).ShouldBe(
            ["Daml.Build.Core.dll", "Daml.Build.Tasks.dll", "YamlDotNet.dll"], ignoreOrder: true);

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_package_builds_and_hands_on_its_DAR(MSBuildHost host)
    {
        var package = Path.Combine(_sandbox.Root, "contracts");
        var created = BuildCommand("Compiling contracts to a DAR.\nCreated .daml\\dist\\contracts-1.2.0.dar\n");
        created.WriteFiles[@".daml\dist\contracts-1.2.0.dar"] = "dar";
        Scenario(created);

        var run = BuildWithFake(host, Package("contracts"));

        run.ExitCode.ShouldBe(0, run.Log);
        run.Log.ShouldContain($@"DAR: {package}\.daml\dist\contracts-1.2.0.dar");
        Calls("build").ShouldHaveSingleItem().WorkingDirectory.ShouldBe(package);
        // The host the test meant: .NET Framework MSBuild, or the .NET 10 SDK global.json pins.
        if (host == MSBuildHost.MSBuildExe)
        {
            run.Log.ShouldContain("Host: Full ");
        }
        else
        {
            run.Log.ShouldMatch(@"Host: Core .*\\sdk\\10\.0\.\d+\\");
        }
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void Compile_errors_come_out_in_the_canonical_format_with_their_range(MSBuildHost host)
    {
        var package = Path.Combine(_sandbox.Root, "broken");
        Scenario(BuildCommand(File.ReadAllText(Path.Combine(Repository.Root, @"test\fixtures\build-output\build-broken.stderr.txt")), exitCode: 1));

        var run = BuildWithFake(host, Package("broken"));

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain($@"{package}\daml\SyntaxError.daml(6,1,6,1): parser error DAML0001: parse error (possibly incorrect indentation or mismatched brackets)");
        run.Log.ShouldContain($@"{package}\daml\TypeError.daml(5,9,5,20): typecheck error DAML0001: • Couldn't match expected type ‘Int’ with actual type ‘Text’");
        run.Log.ShouldContain($@"{package}\daml\UnknownImport.daml(4,8,4,23): not found error DAML0001: Could not find module ‘DA.DoesNotExist’");
        run.Log.ShouldNotContain("MSB6006");
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_manifest_dpm_couldnt_build_from_stops_the_build_before_dpm_runs(MSBuildHost host)
    {
        Scenario(BuildCommand(""));

        var run = BuildWithFake(host, Package("contracts", "version: 1.2.0\nsource: daml\n"));

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain("error DAML0100: daml.yaml has no 'name'; dpm needs one to build the package.");
        _dpm.ReadCalls().ShouldBeEmpty();
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_dpm_past_its_time_limit_is_stopped_with_the_build(MSBuildHost host)
    {
        Scenario(BuildCommand("Compiling contracts to a DAR.\n", delay: 120_000));

        var run = BuildWithFake(host, Package("contracts"), ("DamlProcessTimeoutSeconds", "2"));

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain("error DAML0110: dpm build didn't finish within 2 s");
        run.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
        var build = Calls("build").ShouldHaveSingleItem();
        SpinWaitUntilGone(build.ProcessId);
    }

    // One MSBuild process, two packages: the tasks load once, dpm is asked its version once.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void Two_packages_in_one_build_ask_dpm_its_version_once(MSBuildHost host)
    {
        var created = BuildCommand("Created .daml\\dist\\contracts-1.2.0.dar\n");
        Scenario(created);
        Package("first");
        Package("second");
        _sandbox.Write("all.proj", """
            <Project>
              <Target Name="Build">
                <MSBuild Projects="first\Contracts.proj;second\Contracts.proj" Targets="Build" />
              </Target>
            </Project>
            """);

        var run = BuildWithFake(host, "all.proj");

        run.ExitCode.ShouldBe(0, run.Log);
        run.Log.ShouldContain($@"DAR: {_sandbox.Root}\first\.daml\dist\contracts-1.2.0.dar");
        run.Log.ShouldContain($@"DAR: {_sandbox.Root}\second\.daml\dist\contracts-1.2.0.dar");
        Calls("build").Count.ShouldBe(2);
        Calls("--version").Count.ShouldBe(1);
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_real_package_builds_its_DAR_on_each_host(MSBuildHost host)
    {
        var hostPath = HostPath(host);
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var package = CopySample("single-package");

        var run = _sandbox.Build(host, hostPath, Path.Combine(package, "Contracts.proj"), properties: new Dictionary<string, string> { ["DamlDpmPath"] = dpm });

        run.ExitCode.ShouldBe(0, run.Log);
        File.Exists(Path.Combine(_sandbox.Root, package, @".daml\dist\single-package-0.0.1.dar")).ShouldBeTrue(run.Log);
        run.Log.ShouldContain("warning DAML0002: warning while type checking package");
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_real_broken_package_reports_its_errors_where_they_are(MSBuildHost host)
    {
        var hostPath = HostPath(host);
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var package = CopySample("broken");

        var run = _sandbox.Build(host, hostPath, Path.Combine(package, "Contracts.proj"), properties: new Dictionary<string, string> { ["DamlDpmPath"] = dpm });

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain($@"{Path.Combine(_sandbox.Root, package)}\daml\TypeError.daml(5,9,5,20): typecheck error DAML0001: • Couldn't match expected type ‘Int’ with actual type ‘Text’");
        run.Log.ShouldNotContain("MSB6006");
    }

    // A copy without earlier build output, with the package project next to its daml.yaml.
    private string CopySample(string name)
    {
        var source = Path.Combine(Repository.Root, "samples", name);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            if (relative.StartsWith(".daml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var target = Path.Combine(_sandbox.Root, name, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        _sandbox.Write(Path.Combine(name, "Contracts.proj"), PackageProject);
        return name;
    }

    private static void SpinWaitUntilGone(int processId)
    {
        bool Running()
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        System.Threading.SpinWait.SpinUntil(() => !Running(), TimeSpan.FromSeconds(10)).ShouldBeTrue("the fake dpm is still running");
    }
}
