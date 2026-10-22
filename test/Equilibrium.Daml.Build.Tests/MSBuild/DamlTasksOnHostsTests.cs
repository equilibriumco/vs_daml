using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Tests.Codegen;
using Equilibrium.Daml.Build.Tests.FileSystem;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.MSBuild;

// The tasks as MSBuild loads them: from a folder of their own, in MSBuild.exe of Visual Studio
// 2026 (.NET Framework) and in dotnet msbuild (.NET 10), with no Visual Studio around.
public sealed class DamlTasksOnHostsTests : IDisposable
{
    private const string PackageProject = """
        <Project>
          <UsingTask TaskName="Equilibrium.Daml.Build.Tasks.ReadDamlManifest" AssemblyFile="$(EqDamlTasksAssembly)" />
          <UsingTask TaskName="Equilibrium.Daml.Build.Tasks.DamlBuildTask" AssemblyFile="$(EqDamlTasksAssembly)" />
          <Target Name="Build">
            <ReadDamlManifest ManifestPath="$(MSBuildProjectDirectory)\daml.yaml">
              <Output TaskParameter="DarPath" PropertyName="_EqDamlDar" />
            </ReadDamlManifest>
            <DamlBuildTask ManifestPath="$(MSBuildProjectDirectory)\daml.yaml" DarPath="$(_EqDamlDar)" DpmPath="$(EqDamlDpmPath)" ProcessTimeoutSeconds="$(EqDamlProcessTimeoutSeconds)">
              <Output TaskParameter="CreatedDar" PropertyName="_EqDamlCreatedDar" />
            </DamlBuildTask>
            <Message Importance="high" Text="DAR: $(_EqDamlCreatedDar)" />
            <Message Importance="high" Text="Host: $(MSBuildRuntimeType) $(MSBuildExtensionsPath)" />
          </Target>
        </Project>
        """;

    private const string Manifest = "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n";

    private readonly MSBuildSandbox _sandbox = new();
    private readonly FakeDpm _dpm = FakeDpm.Create(new FakeDpmScenario());

    // As short as a real one: blob paths in a generator cache are long, and the 32-bit MSBuild.exe can't take more than 260 characters.
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), $"dla-{Guid.NewGuid():N}".Substring(0, 12));

    public void Dispose()
    {
        _dpm.Dispose();
        _sandbox.Dispose();
        DeepPath.Delete(_localAppData);
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
        all["EqDamlDpmPath"] = _dpm.ExecutablePath;
        return _sandbox.Build(host, HostPath(host), project, _dpm.Environment, all);
    }

    private List<FakeDpmCall> Calls(string verb) => _dpm.ReadCalls().Where(c => c.Args.Count == 1 && c.Args[0] == verb).ToList();

    // What a package ships next to its tasks: no MSBuild or framework assembly, which the host
    // provides and a second copy would clash with.
    [Fact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void The_tasks_folder_holds_the_tasks_and_their_own_dependencies_only() =>
        Directory.GetFiles(_sandbox.Tasks).Select(Path.GetFileName).ShouldBe(
            ["Equilibrium.Daml.Build.Core.dll", "Equilibrium.Daml.Build.Tasks.dll", "YamlDotNet.dll"], ignoreOrder: true);

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
        run.Log.ShouldContain($@"{package}\daml\SyntaxError.daml(6,1,6,1): parser error EQD0001: parse error (possibly incorrect indentation or mismatched brackets)");
        run.Log.ShouldContain($@"{package}\daml\TypeError.daml(5,9,5,20): typecheck error EQD0001: • Couldn't match expected type ‘Int’ with actual type ‘Text’");
        run.Log.ShouldContain($@"{package}\daml\UnknownImport.daml(4,8,4,23): not found error EQD0001: Could not find module ‘DA.DoesNotExist’");
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
        run.Log.ShouldContain("error EQD0100: daml.yaml has no 'name'; dpm needs one to build the package.");
        _dpm.ReadCalls().ShouldBeEmpty();
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_dpm_past_its_time_limit_is_stopped_with_the_build(MSBuildHost host)
    {
        Scenario(BuildCommand("Compiling contracts to a DAR.\n", delay: 120_000));

        var run = BuildWithFake(host, Package("contracts"), ("EqDamlProcessTimeoutSeconds", "2"));

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain("error EQD0110: dpm build didn't finish within 2 s");
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

        var run = _sandbox.Build(host, hostPath, Path.Combine(package, "Contracts.proj"), properties: new Dictionary<string, string> { ["EqDamlDpmPath"] = dpm });

        run.ExitCode.ShouldBe(0, run.Log);
        File.Exists(Path.Combine(_sandbox.Root, package, @".daml\dist\single-package-0.0.1.dar")).ShouldBeTrue(run.Log);
        run.Log.ShouldContain("warning EQD0002: warning while type checking package");
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_real_broken_package_reports_its_errors_where_they_are(MSBuildHost host)
    {
        var hostPath = HostPath(host);
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var package = CopySample("broken");

        var run = _sandbox.Build(host, hostPath, Path.Combine(package, "Contracts.proj"), properties: new Dictionary<string, string> { ["EqDamlDpmPath"] = dpm });

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain($@"{Path.Combine(_sandbox.Root, package)}\daml\TypeError.daml(5,9,5,20): typecheck error EQD0001: • Couldn't match expected type ‘Int’ with actual type ‘Text’");
        run.Log.ShouldNotContain("MSB6006");
    }

    // A C# project's codegen step on its own, with one DAR reference and the generated files printed.
    private const string AppProject = """
        <Project>
          <UsingTask TaskName="Equilibrium.Daml.Build.Tasks.DamlCodegenCSharp" AssemblyFile="$(EqDamlTasksAssembly)" />
          <UsingTask TaskName="Equilibrium.Daml.Build.Tasks.DamlCleanGenerated" AssemblyFile="$(EqDamlTasksAssembly)" />
          <ItemGroup>
            <DamlRef Include="$(DamlDarFile)" EqDamlReferenceSource="EqDamlDar" />
          </ItemGroup>
          <Target Name="Build">
            <DamlCodegenCSharp ProjectPath="$(MSBuildProjectFullPath)" IntermediateOutputPath="obj\Debug\net10.0\" Configuration="Debug" TargetFramework="net10.0"
                References="@(DamlRef)" DesignTime="$(DamlDesignTime)" DpmPath="$(EqDamlDpmPath)" JavaHome="$(EqDamlJavaHome)"
                AllowToolInstall="$(EqDamlAllowToolInstall)" RuntimeVersion="$(DamlRuntimeVersion)" LedgerAbstractionsVersion="0.6.0" CodegenComponent="$(EqDamlCodegenComponent)"
                SkipPreflight="$(EqDamlSkipPreflight)">
              <Output TaskParameter="GeneratedFiles" ItemName="_EqDamlGenerated" />
            </DamlCodegenCSharp>
            <Message Importance="high" Text="Generated: @(_EqDamlGenerated->'%(Filename)%(Extension)')" />
          </Target>
          <Target Name="CleanGenerated">
            <DamlCleanGenerated ProjectPath="$(MSBuildProjectFullPath)" IntermediateOutputPath="obj\Debug\net10.0\" />
          </Target>
        </Project>
        """;

    // Without the runtime package the preflight stops the build; that it says so shows the task,
    // its core and their dependencies load and run in each host.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void The_codegen_task_runs_on_each_host_and_reports_what_the_preflight_finds(MSBuildHost host)
    {
        Scenario();
        _sandbox.Write(@"vendor\finance-1.0.0.dar", "a DAR the preflight only hashes");
        var app = _sandbox.Write(@"App\App.proj", AppProject);

        var run = BuildWithFake(host, app, ("DamlDarFile", Path.Combine(_sandbox.Root, @"vendor\finance-1.0.0.dar")));

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain("error EQD0201");
        run.Log.ShouldNotContain("MSB4018");
        Calls("codegen-cs").ShouldBeEmpty();
    }

    // The whole generation with a fake generator, so the codegen task and its core work through
    // publishing in each host, the 32-bit MSBuild.exe with its old .NET Framework path rules among
    // them. The machine's own generator cache and locks stay out of it.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_DAR_turns_into_bindings_on_each_host_and_the_next_build_runs_nothing(MSBuildHost host)
    {
        var (app, environment, properties) = FakeCodegen();

        var first = _sandbox.Build(host, HostPath(host), app, environment, properties);
        var second = _sandbox.Build(host, HostPath(host), app, environment, properties);

        first.ExitCode.ShouldBe(0, first.Log);
        first.Log.ShouldContain("Generated C# for");
        first.Log.ShouldContain("Generated: Asset.cs;Asset.Give.cs");
        second.ExitCode.ShouldBe(0, second.Log);
        second.Log.ShouldNotContain("Generated C# for");
        second.Log.ShouldContain("Generated: Asset.cs;Asset.Give.cs");
        _dpm.ReadCalls().Count(c => c.Args.Count > 0 && c.Args[0] == "codegen-cs").ShouldBe(1);

        var clean = _sandbox.Build(host, HostPath(host), app, environment, properties, target: "CleanGenerated");

        clean.ExitCode.ShouldBe(0, clean.Log);
        clean.Log.ShouldContain("Removed the generated C# in");
        Directory.Exists(Path.Combine(_sandbox.Root, @"App\obj\Debug\net10.0\Daml")).ShouldBeFalse();
    }

    // Two processes building one project at once, as two command lines may: the lock on its
    // generation root lets one run the generator, and the other compiles what it published.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public async Task Two_builds_of_one_project_at_once_on_each_host_run_the_generator_once(MSBuildHost host)
    {
        var (app, environment, properties) = FakeCodegen(generatorDelay: 1500);

        var builds = await Task.WhenAll(
            Task.Run(() => _sandbox.Build(host, HostPath(host), app, environment, properties)),
            Task.Run(() => _sandbox.Build(host, HostPath(host), app, environment, properties)));

        builds.ShouldAllBe(b => b.ExitCode == 0, string.Join("\n----\n", builds.Select(b => b.Log)));
        builds.ShouldAllBe(b => b.Log.Contains("Generated: Asset.cs;Asset.Give.cs"));
        builds.Count(b => b.Log.Contains("Generated C# for")).ShouldBe(1);
        _dpm.ReadCalls().Count(c => c.Args.Count > 0 && c.Args[0] == "codegen-cs").ShouldBe(1);
    }

    // The codegen task with a fake generator that takes generatorDelay ms, and a synthetic generator
    // cache and the locks in a LOCALAPPDATA of their own, away from the machine's.
    private (string App, Dictionary<string, string> Environment, Dictionary<string, string> Properties) FakeCodegen(int generatorDelay = 0)
    {
        var cache = SyntheticGeneratorCache.Create(Path.Combine(_localAppData, @"EquilibriumDamlTools\codegen"));
        new GeneratorCache(cache.Root, cache.Pin).MarkReady();
        var generator = new FakeDpmCommand { DelayMilliseconds = generatorDelay };
        generator.Args.AddRange(["codegen-cs", "--dar", "*", "--out", "*"]);
        generator.WriteFiles["{out}/MiniDemo/Asset/Asset.cs"] = "public sealed partial record Asset;\n";
        generator.WriteFiles["{out}/MiniDemo/Asset/Asset.Give.cs"] = "public sealed partial record Asset;\n";
        Scenario(generator);
        var dar = Path.Combine(_sandbox.Root, @"vendor\finance-1.0.0.dar");
        TestDar.Write(dar);
        // Never run: the version checks are skipped.
        _sandbox.Write(@"jdk\bin\java.exe", "");
        var app = _sandbox.Write(@"App\App.proj", AppProject);
        var environment = new Dictionary<string, string>(_dpm.Environment.ToDictionary(e => e.Key, e => e.Value)) { ["LOCALAPPDATA"] = _localAppData };
        var properties = new Dictionary<string, string>
        {
            ["DamlDarFile"] = dar,
            ["EqDamlDpmPath"] = _dpm.ExecutablePath,
            ["EqDamlJavaHome"] = Path.Combine(_sandbox.Root, "jdk"),
            ["DamlRuntimeVersion"] = "0.6.0",
            ["EqDamlCodegenComponent"] = cache.Pin.Reference,
            ["EqDamlSkipPreflight"] = "true",
        };
        return (app, environment, properties);
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_design_time_build_on_each_host_runs_nothing_and_says_to_build(MSBuildHost host)
    {
        Scenario();
        var app = _sandbox.Write(@"App\App.proj", AppProject);

        var run = BuildWithFake(host, app, ("DamlDarFile", Path.Combine(_sandbox.Root, @"vendor\finance-1.0.0.dar")), ("DamlDesignTime", "true"));

        run.ExitCode.ShouldBe(0, run.Log);
        run.Log.ShouldContain("build the project to generate it");
        _dpm.ReadCalls().ShouldBeEmpty();
        Directory.Exists(Path.Combine(_sandbox.Root, @"App\obj")).ShouldBeFalse();
    }

    // The generator's own mini demo: its DAR built by the package task, then turned into C# by the
    // codegen task, both in the same host; the second build finds the bindings current.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_real_DAR_turns_into_its_bindings_on_each_host_and_the_next_build_runs_nothing(MSBuildHost host)
    {
        var hostPath = HostPath(host);
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        var package = CopyPackage(Path.Combine(Repository.Root, @"third_party\canton-dotnet-sdk-mini-demo\daml"), "demo");
        _sandbox.Build(host, hostPath, Path.Combine(package, "Contracts.proj"), properties: new Dictionary<string, string> { ["EqDamlDpmPath"] = dpm })
            .ExitCode.ShouldBe(0);
        var app = _sandbox.Write(@"App\App.proj", AppProject);
        var properties = new Dictionary<string, string>
        {
            ["DamlDarFile"] = Path.Combine(_sandbox.Root, @"demo\.daml\dist\canton-mini-demo-hbde402d4f83a-0.1.0.dar"),
            ["EqDamlDpmPath"] = dpm,
            ["EqDamlJavaHome"] = Path.GetDirectoryName(Path.GetDirectoryName(java))!,
            // Installs the generator once on a machine that hasn't got it yet.
            ["EqDamlAllowToolInstall"] = "true",
            ["DamlRuntimeVersion"] = "0.6.0",
        };

        var first = _sandbox.Build(host, hostPath, app, properties: properties);
        var second = _sandbox.Build(host, hostPath, app, properties: properties);

        first.ExitCode.ShouldBe(0, first.Log);
        first.Log.ShouldContain("Generated C# for");
        first.Log.ShouldContain("PackageRegistration_1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb.cs");
        Directory.GetFiles(Path.Combine(_sandbox.Root, @"App\obj\Debug\net10.0\Daml"), "*.cs", SearchOption.AllDirectories).Length.ShouldBe(7);
        second.ExitCode.ShouldBe(0, second.Log);
        second.Log.ShouldNotContain("Generated C# for");
        second.Log.ShouldContain("PackageRegistration_1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb.cs");
    }

    // A copy without earlier build output, with the package project next to its daml.yaml.
    private string CopySample(string name) => CopyPackage(Path.Combine(Repository.Root, "samples", name), name);

    private string CopyPackage(string source, string name)
    {
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
