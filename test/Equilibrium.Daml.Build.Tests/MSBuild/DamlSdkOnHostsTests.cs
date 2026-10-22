using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.MSBuild;

// Daml projects as users write them, <Project Sdk="Equilibrium.Daml.Sdk/…">, with NuGet resolving the SDK
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

    private string DamlProject(string manifest = Manifest, string body = "")
    {
        _sandbox.UseSdkFeed();
        _sandbox.Write(@"contracts\daml.yaml", manifest);
        _sandbox.Write(@"contracts\daml\Main.daml", "module Main where\n");
        return _sandbox.Write(@"contracts\Contracts.msbuildproj", $"<Project Sdk=\"Equilibrium.Daml.Sdk/{SdkFeed.Version}\">\n{body}\n</Project>\n");
    }

    private const string ShowItems = """
          <Target Name="ShowItems">
            <Message Importance="high" Text="Source: %(EqDamlSource.FullPath)" />
            <Message Importance="high" Text="Shown: %(None.FullPath)" />
          </Target>
        """;

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
        all["EqDamlDpmPath"] = _dpm.ExecutablePath;
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
        var dar = Build(host, project, target: "EqDamlGetDar", ("DesignTimeBuild", "true"));

        restore.ExitCode.ShouldBe(0, restore.Log);
        designTime.ExitCode.ShouldBe(0, designTime.Log);
        dar.ExitCode.ShouldBe(0, dar.Log);
        _dpm.ReadCalls().ShouldBeEmpty();
    }

    // What Solution Explorer shows comes from the manifest's source, never from damlc's or the build's own folders.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe, "source: daml")]
    [InlineData(MSBuildHost.Dotnet, "source: \"daml\"  # quoted, with a comment")]
    [InlineData(MSBuildHost.MSBuildExe, "source: ./daml/Main.daml")]
    [InlineData(MSBuildHost.Dotnet, "source: .")]
    public void The_project_shows_its_manifest_and_sources_and_no_build_output(MSBuildHost host, string source)
    {
        var project = DamlProject($"sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\n{source}\n", ShowItems);
        foreach (var file in new[] { @"daml\Asset\Asset.daml", @".daml\package-database\3.6.0\daml-stdlib\DA\List.daml", @"obj\Generated.daml", @"bin\Copied.daml" })
        {
            _sandbox.Write(Path.Combine("contracts", file), "module M where\n");
        }

        var run = Build(host, project, target: "ShowItems");

        run.ExitCode.ShouldBe(0, run.Log);
        var sources = run.Log.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Source: ", StringComparison.Ordinal)).ToList();
        sources.ShouldBe([$@"Source: {Package}\daml\Asset\Asset.daml", $@"Source: {Package}\daml\Main.daml"], ignoreOrder: true);
        run.Log.ShouldContain($@"Shown: {Package}\daml.yaml");
        run.Log.ShouldContain($@"Shown: {Package}\daml\Main.daml");
    }

    // What the DAR is built from decides, not file times: a deleted source is noticed too (S-06).
    // Debug and Release share the package's DAR. Both find it out of date and one waits for the other;
    // that one finds the DAR the first made from the same inputs and runs no dpm, so nothing rewrites
    // it while a project using it reads it. Visual Studio's own build of a Daml project and that of a
    // C# project needing its DAR meet the same way.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public async System.Threading.Tasks.Task Debug_and_Release_built_at_once_run_dpm_once(MSBuildHost host)
    {
        var slow = CreatesTheDar();
        slow.DelayMilliseconds = 1500;
        Scenario(slow);
        var project = DamlProject();
        var properties = new Dictionary<string, string> { ["EqDamlDpmPath"] = _dpm.ExecutablePath };
        var restore = _sandbox.Build(host, HostPath(host), project, _dpm.Environment, properties, "Restore", restore: false);

        var runs = await System.Threading.Tasks.Task.WhenAll(
            new[] { "Debug", "Release" }.Select(configuration => System.Threading.Tasks.Task.Run(() =>
                _sandbox.Build(host, HostPath(host), project, _dpm.Environment, new Dictionary<string, string>(properties) { ["Configuration"] = configuration }, restore: false))));

        restore.ExitCode.ShouldBe(0, restore.Log);
        runs.ShouldAllBe(r => r.ExitCode == 0, string.Join("\n----\n", runs.Select(r => r.Log)));
        Calls("build").Count.ShouldBe(1);
        runs.Count(r => r.Log.Contains("by another build while this one waited")).ShouldBe(1);
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_second_build_runs_no_dpm_and_a_deleted_source_builds_again(MSBuildHost host)
    {
        Scenario(CreatesTheDar());
        var project = DamlProject();
        var asset = _sandbox.Write(@"contracts\daml\Asset.daml", "module Asset where\n");

        var first = Build(host, project);
        var second = Build(host, project);
        File.Delete(asset);
        var third = Build(host, project);

        (first.ExitCode, second.ExitCode, third.ExitCode).ShouldBe((0, 0, 0), first.Log + second.Log + third.Log);
        Calls("build").Count.ShouldBe(2);
        second.Log.ShouldContain("is up to date");
        second.Log.ShouldContain($@"Contracts -> {Package}\.daml\dist\contracts-1.2.0.dar");
    }

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void DamlBuildEnabled_off_runs_no_dpm_and_a_value_it_cant_use_is_DAML0100(MSBuildHost host)
    {
        Scenario(CreatesTheDar());
        var project = DamlProject();

        var off = Build(host, project, null, ("EqDamlBuildEnabled", "off"));
        var unusable = Build(host, project, null, ("EqDamlBuildEnabled", "sometimes"));

        off.ExitCode.ShouldBe(0, off.Log);
        unusable.ExitCode.ShouldBe(1, unusable.Log);
        unusable.Log.ShouldContain("error DAML0100: EqDamlBuildEnabled is 'sometimes'");
        _dpm.ReadCalls().ShouldBeEmpty();
    }

    // The package's own outputs go; the rest of its .daml folder, dpm's caches among it, stays.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void Clean_removes_the_DAR_and_the_build_record_and_the_next_build_builds_again(MSBuildHost host)
    {
        Scenario(CreatesTheDar());
        var project = DamlProject();
        var cache = _sandbox.Write(@"contracts\.daml\package-database\3.6.0\cached.dalf", "kept");

        var build = Build(host, project);
        var clean = Build(host, project, target: "Clean");
        var after = Build(host, project);

        (build.ExitCode, clean.ExitCode, after.ExitCode).ShouldBe((0, 0, 0), build.Log + clean.Log + after.Log);
        File.Exists(cache).ShouldBeTrue();
        Calls("build").Count.ShouldBe(2);
    }

    // What Visual Studio's fast up-to-date check is told, also through its own design-time targets.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe, "EqDamlCollectUpToDateCheckItems")]
    [InlineData(MSBuildHost.Dotnet, "EqDamlCollectUpToDateCheckItems")]
    [InlineData(MSBuildHost.MSBuildExe, "CollectUpToDateCheckInputDesignTime;CollectUpToDateCheckBuiltDesignTime")]
    public void The_up_to_date_check_knows_what_the_DAR_is_built_from(MSBuildHost host, string targets)
    {
        var multiPackage = _sandbox.Write("multi-package.yaml", "packages: [contracts]\n");
        var dependency = _sandbox.Write(@"lib\dependency.dar", "dependency");
        var project = DamlProject(manifest: Manifest + "dependencies:\n- ../lib/dependency.dar\n", body: $"""
              <Target Name="ShowUpToDateItems" DependsOnTargets="{targets}">
                <Message Importance="high" Text="Input: %(UpToDateCheckInput.FullPath)" />
                <Message Importance="high" Text="Built: %(UpToDateCheckBuilt.FullPath)" />
                <Message Importance="high" Text="Check disabled: $(DisableFastUpToDateCheck)" />
              </Target>
            """);

        var run = Build(host, project, "ShowUpToDateItems", ("DesignTimeBuild", "true"));

        run.ExitCode.ShouldBe(0, run.Log);
        // MSBuild must see changed environment and newly created ancestor manifests.
        run.Log.ShouldContain("Check disabled: true");
        System.Text.RegularExpressions.Regex.Matches(run.Log, @"Built: (\S+)").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct()
            .ShouldBe([$@"{Package}\.daml\dist\contracts-1.2.0.dar", $@"{Package}\obj\daml.build.state"], ignoreOrder: true);
        run.Log.ShouldContain($@"Input: {Package}\daml.yaml");
        run.Log.ShouldContain($"Input: {multiPackage}");
        run.Log.ShouldContain($"Input: {dependency}");
        run.Log.ShouldContain($@"Input: {Package}\daml\Main.daml");
        run.Log.ShouldContain($@"Built: {Package}\.daml\dist\contracts-1.2.0.dar");
        run.Log.ShouldContain($@"Built: {Package}\obj\daml.build.state");
        _dpm.ReadCalls().ShouldBeEmpty();
    }

    // Only on request, and after the build it needs.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void DamlTest_builds_first_and_reports_a_failing_script_where_it_is(MSBuildHost host)
    {
        var test = BuildCommand(File.ReadAllText(Path.Combine(Repository.Root, @"test\fixtures\build-output\test-failing-script.stderr.txt")), exitCode: 1);
        test.Args[0] = "test";
        test.Stdout = File.ReadAllText(Path.Combine(Repository.Root, @"test\fixtures\build-output\test-failing-script.stdout.txt"));
        Scenario(CreatesTheDar(), test);

        var run = Build(host, DamlProject(), target: "EqDamlTest");

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain($@"{Package}\daml\Main.daml(47,");
        run.Log.ShouldContain("Script error DAML0001");
        _dpm.ReadCalls().Where(c => c.Args.Count == 1).Select(c => c.Args[0]).Where(v => v != "--version").ShouldBe(["build", "test"]);
    }

    // Projects of one solution a release apart: MSBuild.exe loads the tasks of both from two folders
    // under one assembly identity, and the first loaded serves both, which task parameters that are
    // only ever added keep working.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void Two_patches_of_the_SDK_build_side_by_side_in_one_build(MSBuildHost host)
    {
        Scenario(CreatesTheDar());
        var first = DamlProject();
        _sandbox.Write(@"other\daml.yaml", Manifest);
        _sandbox.Write(@"other\daml\Main.daml", "module Main where\n");
        var second = _sandbox.Write(@"other\Contracts.msbuildproj", $"<Project Sdk=\"Equilibrium.Daml.Sdk/{SdkFeed.NextPatch}\">\n</Project>\n");
        var both = _sandbox.Write("all.proj", """
            <Project>
              <Target Name="Build">
                <MSBuild Projects="contracts\Contracts.msbuildproj;other\Contracts.msbuildproj" Targets="Build" />
              </Target>
            </Project>
            """);
        Build(host, first, target: "Restore").ExitCode.ShouldBe(0);
        Build(host, second, target: "Restore").ExitCode.ShouldBe(0);

        var all = _sandbox.Build(host, HostPath(host), both, _dpm.Environment, new Dictionary<string, string> { ["EqDamlDpmPath"] = _dpm.ExecutablePath }, restore: false);

        all.ExitCode.ShouldBe(0, all.Log);
        all.Log.ShouldContain($@"Contracts -> {Package}\.daml\dist\contracts-1.2.0.dar");
        all.Log.ShouldContain($@"Contracts -> {Path.Combine(_sandbox.Root, "other")}\.daml\dist\contracts-1.2.0.dar");
        Calls("build").Count.ShouldBe(2);
    }

    // The sample a C# project consumes, built for real through the SDK on each host.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void The_mixed_dotnet_sample_builds_through_the_SDK_and_names_its_package(MSBuildHost host)
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var hostPath = HostPath(host);
        _sandbox.UseSdkFeed();
        var source = Repository.Sample(@"mixed-dotnet\daml");
        // Without earlier build output, the nested test package's included; the project below replaces the sample's own.
        string[] output = [".daml", "obj", "bin"];
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length + 1);
            if (!relative.Split('\\').Any(s => output.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                var target = Path.Combine(Package, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        var project = _sandbox.Write(@"contracts\Contracts.msbuildproj", $$"""
            <Project Sdk="Equilibrium.Daml.Sdk/{{SdkFeed.Version}}">
              <Target Name="ShowDar">
                <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="EqDamlGetDar">
                  <Output TaskParameter="TargetOutputs" ItemName="_Dar" />
                </MSBuild>
                <Message Importance="high" Text="Package id: %(_Dar.EqDamlMainPackageId), built from: %(_Dar.EqDamlSourceFingerprint)" />
              </Target>
            </Project>
            """);
        var properties = new Dictionary<string, string> { ["EqDamlDpmPath"] = dpm };

        var first = _sandbox.Build(host, hostPath, project, properties: properties);
        var second = _sandbox.Build(host, hostPath, project, properties: properties, target: "Build;ShowDar");

        first.ExitCode.ShouldBe(0, first.Log);
        first.Log.ShouldContain($@"Contracts -> {Package}\.daml\dist\mixed-dotnet-contracts-0.1.0.dar");
        second.ExitCode.ShouldBe(0, second.Log);
        second.Log.ShouldContain("is up to date");
        second.Log.ShouldMatch("Package id: [0-9a-f]{64}, built from: [0-9a-f]{64}");
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
                <MSBuild Projects="@(ProjectReference)" Targets="EqDamlGetDar">
                  <Output TaskParameter="TargetOutputs" ItemName="_EqDamlDar" />
                </MSBuild>
                <Message Importance="high" Text="DAR: @(_EqDamlDar) exists=%(_EqDamlDar.Exists) package=%(_EqDamlDar.EqDamlPackageName)-%(_EqDamlDar.EqDamlPackageVersion)" />
                <Message Importance="high" Text="Built from: %(_EqDamlDar.EqDamlSourceFingerprint) per %(_EqDamlDar.EqDamlManifest)" />
              </Target>
            </Project>
            """);

        var run = Build(host, app);

        run.ExitCode.ShouldBe(0, run.Log);
        run.Log.ShouldContain($@"DAR: {Package}\.daml\dist\contracts-1.2.0.dar exists=True package=contracts-1.2.0");
        // The sources the DAR's build recorded, for navigation to trust them.
        run.Log.ShouldMatch($@"Built from: [0-9a-f]{{64}} per {System.Text.RegularExpressions.Regex.Escape(Package)}\\daml\.yaml");
        Calls("build").ShouldHaveSingleItem();
    }
}
