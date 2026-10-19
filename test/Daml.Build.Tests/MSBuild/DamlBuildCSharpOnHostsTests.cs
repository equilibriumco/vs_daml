using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Tests.Codegen;
using Daml.Build.Tests.FileSystem;
using Daml.TestHelpers;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Daml.Build.Tests.MSBuild;

// C# projects as users write them: Daml.Build.CSharp and Daml.Runtime from a local feed, DARs
// from files and from Daml.Sdk projects, built by MSBuild.exe of Visual Studio 2026 and by dotnet
// msbuild, with a fake dpm, a fake generator and a synthetic generator cache.
public sealed class DamlBuildCSharpOnHostsTests : IDisposable
{
    private const string SingleFramework = "<TargetFramework>net10.0</TargetFramework>";

    // What the project's own targets show of the build, for the tests to read from the log.
    private const string ShowTargets = """
          <Target Name="ShowEvaluated">
            <Message Importance="high" Text="Evaluated: %(Compile.FullPath)" Condition="'%(Compile.DamlGenerated)' == 'true'" />
          </Target>
          <Target Name="ShowCompiled" DependsOnTargets="DamlCollectGeneratedCompile">
            <Message Importance="high" Text="Compiled: %(Compile.FullPath)" />
          </Target>
          <Target Name="ShowUpToDateCheck" DependsOnTargets="CollectUpToDateCheckInputDesignTime;CollectUpToDateCheckBuiltDesignTime">
            <Message Importance="high" Text="Input: %(UpToDateCheckInput.FullPath)" />
            <Message Importance="high" Text="Built: %(UpToDateCheckBuilt.FullPath)" />
            <Message Importance="high" Text="Input targets: $(CollectUpToDateCheckInputDesignTimeDependsOn)" />
            <Message Importance="high" Text="Built targets: $(CollectUpToDateCheckBuiltDesignTimeDependsOn)" />
          </Target>
          <Target Name="ShowRuntime">
            <Message Importance="high" Text="Runtime: $(_DamlRuntimeVersion)" />
          </Target>
        """;

    private readonly MSBuildSandbox _sandbox = new();
    private readonly FakeDpm _dpm = FakeDpm.Create(new FakeDpmScenario());
    private readonly ITestOutputHelper _output;

    // As short as a real one: blob paths in a generator cache are long, and the 32-bit MSBuild.exe can't take more than 260 characters.
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), $"dla-{Guid.NewGuid():N}".Substring(0, 12));

    private CodegenComponentPin? _pin;

    public DamlBuildCSharpOnHostsTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        _dpm.Dispose();
        _sandbox.Dispose();
        DeepPath.Delete(_localAppData);
    }

    private static string HostPath(MSBuildHost host) =>
        RequiredTools.Require(host == MSBuildHost.MSBuildExe ? ExternalTool.MSBuild : ExternalTool.Dotnet);

    private string AppFolder => Path.Combine(_sandbox.Root, "App");

    private string GeneratedRoot => Path.Combine(AppFolder, @"obj\Debug\net10.0\Daml");

    private string Props => Path.Combine(AppFolder, @"obj\daml\App.csproj.Debug.net10.0.g.props");

    private string Stamp => Path.Combine(AppFolder, @"obj\daml\App.csproj.Debug.net10.0.built");

    // The C# project, with the package and the runtime from the feed and whatever the test adds.
    private string App(string items, string properties = "", string frameworks = SingleFramework, string? version = null)
    {
        if (_pin is null)
        {
            var cache = SyntheticGeneratorCache.Create(Path.Combine(_localAppData, @"DamlVisualStudio\codegen"));
            new GeneratorCache(cache.Root, cache.Pin).MarkReady();
            _pin = cache.Pin;
            _sandbox.UseSdkFeed();
            // Never run: the version checks are skipped.
            _sandbox.Write(@"jdk\bin\java.exe", "");
        }
        return _sandbox.Write(@"App\App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {frameworks}
                {properties}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Daml.Build.CSharp" Version="{version ?? SdkFeed.Version}" PrivateAssets="all" />
                <PackageReference Include="Daml.Runtime" Version="{SdkFeed.RuntimeVersion}" />
                <PackageReference Include="Daml.Ledger.Abstractions" Version="{SdkFeed.RuntimeVersion}" />
                {items}
              </ItemGroup>
            {ShowTargets}
            </Project>
            """);
    }

    // Hand-written code that needs both generated files of the namespace.
    private void Uses(string @namespace) =>
        _sandbox.Write(@"App\Uses.cs", $"internal static class Uses\n{{\n    public const string Give = {@namespace}.Asset.Asset.Give;\n}}\n");

    private string Dar(string relativePath, string package)
    {
        var path = Path.Combine(_sandbox.Root, relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        TestDar.Write(path, package);
        return path;
    }

    // The fake generator: Asset.cs, and Asset.Give.cs unless give is false, under the namespace's folders.
    private static FakeDpmCommand Generator(string? @namespace, bool give = true)
    {
        var command = new FakeDpmCommand();
        command.Args.AddRange(["codegen-cs", "--dar", "*", "--out", "*"]);
        if (@namespace is not null)
        {
            command.Args.AddRange(["--", "-n", @namespace]);
        }
        var root = @namespace ?? "MiniDemo";
        var folder = "{out}/" + root.Replace('.', '/') + "/Asset/";
        command.WriteFiles[folder + "Asset.cs"] = $"namespace {root}.Asset {{ public sealed partial class Asset {{ }} }}\n";
        if (give)
        {
            command.WriteFiles[folder + "Asset.Give.cs"] = $"namespace {root}.Asset {{ public sealed partial class Asset {{ public const string Give = \"give\"; }} }}\n";
        }
        return command;
    }

    // A fake dpm build that writes a real DAR, as the Daml project's build would; another package name makes another DAR.
    private static FakeDpmCommand BuildsTheDar(string package = "contracts-1.2.0")
    {
        var command = new FakeDpmCommand { Stderr = "Compiling contracts to a DAR.\nCreated .daml\\dist\\contracts-1.2.0.dar\n" };
        command.Args.Add("build");
        var dar = Path.Combine(Path.GetTempPath(), "daml-dar-" + Guid.NewGuid().ToString("N") + ".dar");
        TestDar.Write(dar, package);
        command.WriteFiles[@".daml\dist\contracts-1.2.0.dar"] = "base64:" + Convert.ToBase64String(File.ReadAllBytes(dar));
        File.Delete(dar);
        return command;
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

    // A Daml project on Daml.Sdk next to the C# one.
    private void Contracts(string? sdkVersion = null)
    {
        _sandbox.Write(@"contracts\daml.yaml", "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n");
        _sandbox.Write(@"contracts\daml\Main.daml", "module Main where\n");
        _sandbox.Write(@"contracts\Contracts.msbuildproj", $"<Project Sdk=\"Daml.Sdk/{sdkVersion ?? SdkFeed.Version}\" />\n");
    }

    private MSBuildRun Build(MSBuildHost host, string? target = null, bool restore = true, params (string Name, string Value)[] properties) =>
        BuildProject(@"App\App.csproj", host, target, restore, properties);

    private MSBuildRun BuildProject(string project, MSBuildHost host, string? target = null, bool restore = true, params (string Name, string Value)[] properties)
    {
        var all = new Dictionary<string, string>
        {
            ["DamlDpmPath"] = _dpm.ExecutablePath,
            ["DamlJavaHome"] = Path.Combine(_sandbox.Root, "jdk"),
            ["DamlCodegenComponent"] = _pin!.Reference,
            ["DamlSkipPreflight"] = "true",
        };
        foreach (var (name, value) in properties)
        {
            all[name] = value;
        }
        var environment = _dpm.Environment.ToDictionary(e => e.Key, e => e.Value);
        environment["LOCALAPPDATA"] = _localAppData;
        return _sandbox.Build(host, HostPath(host), project, environment, all, target, restore);
    }

    // What Visual Studio asks of a design-time build: the compiler's inputs, without running it.
    private MSBuildRun DesignTime(MSBuildHost host, string then = "ShowCompiled") =>
        Build(
            host,
            (host == MSBuildHost.MSBuildExe ? "CompileDesignTime;" : "Compile;") + then,
            restore: false,
            ("DesignTimeBuild", "true"),
            ("BuildingInsideVisualStudio", "true"),
            ("SkipCompilerExecution", "true"),
            ("ProvideCommandLineArgs", "true"));

    private int GeneratorRuns() => _dpm.ReadCalls().Count(c => c.Args.Count > 0 && c.Args[0] == "codegen-cs");

    private int DarBuilds() => _dpm.ReadCalls().Count(c => c.Args.Count == 1 && c.Args[0] == "build");

    // The files the props for IntelliSense list.
    private List<string> Listed() =>
        XDocument.Load(Props).Root!.Descendants("Compile").Select(c => (string)c.Attribute("Include")!).ToList();

    private static List<string> Lines(string log, string prefix) =>
        Regex.Matches(log, "^\\s*" + Regex.Escape(prefix) + "(.+?)\\s*$", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_DAR_is_generated_and_compiled_and_the_next_build_runs_no_generator(MSBuildHost host)
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");

        var first = Build(host);
        var listed = Listed();
        var second = Build(host);

        first.ExitCode.ShouldBe(0, first.Log);
        first.Log.ShouldContain("Generated C# for");
        File.Exists(Path.Combine(AppFolder, @"bin\Debug\net10.0\App.dll")).ShouldBeTrue();
        listed.Select(Path.GetFileName).ShouldBe(["Asset.cs", "Asset.Give.cs"], ignoreOrder: true);
        listed.ShouldAllBe(f => f.StartsWith(GeneratedRoot + @"\", StringComparison.OrdinalIgnoreCase) && File.Exists(f));
        File.Exists(Stamp).ShouldBeTrue();
        second.ExitCode.ShouldBe(0, second.Log);
        second.Log.ShouldNotContain("Generated C# for");
        GeneratorRuns().ShouldBe(1);
    }

    // The Daml project builds its DAR through Daml.Sdk, the C# project turns it into C# and compiles
    // it; the packages a patch apart, as in a solution updated one project at a time.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_Daml_project_reference_goes_from_the_SDK_through_the_generator_into_the_compiler(MSBuildHost host)
    {
        App("""<DamlReference Include="..\contracts\Contracts.msbuildproj" Namespace="Contracts" />""", version: SdkFeed.NextPatch);
        Contracts();
        Scenario(BuildsTheDar(), Generator("Contracts"));
        Uses("Contracts");

        var first = Build(host);
        var second = Build(host);

        first.ExitCode.ShouldBe(0, first.Log);
        File.Exists(Path.Combine(AppFolder, @"bin\Debug\net10.0\App.dll")).ShouldBeTrue();
        var manifest = File.ReadAllText(Directory.GetFiles(GeneratedRoot, GenerationManifest.FileName, SearchOption.AllDirectories).ShouldHaveSingleItem());
        manifest.ShouldContain(TestDar.MainPackageId);
        manifest.ShouldContain("sourceManifest=" + Path.Combine(_sandbox.Root, @"contracts\daml.yaml"));
        second.ExitCode.ShouldBe(0, second.Log);
        (DarBuilds(), GeneratorRuns()).ShouldBe((1, 1));
    }

    // Visual Studio builds project references itself, in the order it knows of. Before the first
    // restore it doesn't know the C# project needs the Daml one, and may build both at once: the DAR
    // isn't there yet when the C# project asks for it. The C# project then builds it first, and only
    // that time.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void Inside_Visual_Studio_a_DAR_not_built_yet_is_built_before_generating(MSBuildHost host)
    {
        App("""<DamlReference Include="..\contracts\Contracts.msbuildproj" Namespace="Contracts" />""");
        Contracts();
        Scenario(BuildsTheDar(), Generator("Contracts"));
        Uses("Contracts");
        var restore = Build(host, "Restore", restore: false);
        (string, string)[] insideVisualStudio = [("BuildingInsideVisualStudio", "true")];

        var first = Build(host, null, false, insideVisualStudio);
        var builds = (DarBuilds(), GeneratorRuns());
        var second = Build(host, null, false, insideVisualStudio);

        restore.ExitCode.ShouldBe(0, restore.Log);
        first.ExitCode.ShouldBe(0, first.Log);
        builds.ShouldBe((1, 1));
        var manifest = File.ReadAllText(Directory.GetFiles(GeneratedRoot, GenerationManifest.FileName, SearchOption.AllDirectories).ShouldHaveSingleItem());
        manifest.ShouldContain(TestDar.MainPackageId);
        File.Exists(Path.Combine(AppFolder, @"bin\Debug\net10.0\App.dll")).ShouldBeTrue();
        second.ExitCode.ShouldBe(0, second.Log);
        (DarBuilds(), GeneratorRuns()).ShouldBe((1, 1));
    }

    // What background sync runs after a Daml edit: the Daml project's DAR built again and the bindings
    // regenerated from it, with the runtime known to the generator's checks, but neither the project nor
    // its C# library compiled, and the up-to-date check's stamp left alone, so the next build compiles.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe, """<DamlReference Include="..\contracts\Contracts.msbuildproj" Namespace="Contracts" />""")]
    [InlineData(MSBuildHost.Dotnet, """<ProjectReference Include="..\contracts\Contracts.msbuildproj" DamlNamespace="Contracts" />""")]
    public void A_refresh_regenerates_from_the_new_DAR_and_compiles_nothing(MSBuildHost host, string reference)
    {
        App(reference + """<ProjectReference Include="..\Lib\Lib.csproj" />""");
        _sandbox.Write(@"Lib\Lib.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    {SingleFramework}\n  </PropertyGroup>\n</Project>\n");
        _sandbox.Write(@"Lib\Helper.cs", "public static class Helper { }\n");
        Contracts();
        Scenario(BuildsTheDar(), Generator("Contracts"));
        Uses("Contracts");
        var first = Build(host);
        first.ExitCode.ShouldBe(0, first.Log);
        var app = Path.Combine(AppFolder, @"bin\Debug\net10.0\App.dll");
        var (stamp, compiled, builds, runs) = (File.GetLastWriteTimeUtc(Stamp), File.GetLastWriteTimeUtc(app), DarBuilds(), GeneratorRuns());

        _sandbox.Write(@"contracts\daml\Main.daml", "module Main where\n\n-- edited\n");
        Scenario(BuildsTheDar("contracts-edited"), Generator("Contracts"));
        var refresh = Build(host, "DamlRefreshBindings;ShowRuntime", restore: false, ("BuildProjectReferences", "false"));

        refresh.ExitCode.ShouldBe(0, refresh.Log);
        (DarBuilds() - builds, GeneratorRuns() - runs).ShouldBe((1, 1));
        refresh.Log.ShouldContain("Runtime: " + SdkFeed.RuntimeVersion);
        refresh.Log.ShouldNotContain("App -> ");
        refresh.Log.ShouldNotContain("Lib -> ");
        File.GetLastWriteTimeUtc(Stamp).ShouldBe(stamp);
        File.GetLastWriteTimeUtc(app).ShouldBe(compiled);

        // Background sync left the compiling to the next build, which needs no tool for it.
        var next = Build(host, restore: false);
        next.ExitCode.ShouldBe(0, next.Log);
        File.GetLastWriteTimeUtc(app).ShouldBeGreaterThan(compiled);
        (DarBuilds() - builds, GeneratorRuns() - runs).ShouldBe((1, 1));
    }

    // Visual Studio adds a plain ProjectReference: it brings the DAR and no assembly.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_plain_project_reference_to_a_Daml_project_brings_its_DAR_and_no_assembly(MSBuildHost host)
    {
        App("""<ProjectReference Include="..\contracts\Contracts.msbuildproj" />""");
        Contracts();
        Scenario(BuildsTheDar(), Generator(null));
        Uses("MiniDemo");

        var build = Build(host);
        var designTime = DesignTime(host);

        build.ExitCode.ShouldBe(0, build.Log);
        GeneratorRuns().ShouldBe(1);
        designTime.ExitCode.ShouldBe(0, designTime.Log);
        foreach (var log in new[] { build.Log, designTime.Log })
        {
            log.ShouldNotContain(@"\Contracts.dll");
            log.ShouldNotContain("warning MSB");
            log.ShouldNotContain("warning NU");
        }
        Lines(designTime.Log, "Compiled: ").Where(f => !f.StartsWith(Path.Combine(AppFolder, "obj"), StringComparison.OrdinalIgnoreCase) || f.StartsWith(GeneratedRoot, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).ShouldBe(["Uses.cs", "Asset.cs", "Asset.Give.cs"], ignoreOrder: true);
    }

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void A_new_namespace_without_a_new_DAR_replaces_the_generation()
    {
        Scenario(Generator("Finance"), Generator("Treasury"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Uses("Finance");
        var first = Build(MSBuildHost.MSBuildExe);
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Treasury" />""");
        Uses("Treasury");

        var second = Build(MSBuildHost.MSBuildExe);

        first.ExitCode.ShouldBe(0, first.Log);
        second.ExitCode.ShouldBe(0, second.Log);
        GeneratorRuns().ShouldBe(2);
        var generation = Directory.GetDirectories(GeneratedRoot).ShouldHaveSingleItem();
        Listed().ShouldAllBe(f => f.StartsWith(generation + @"\Treasury\", StringComparison.OrdinalIgnoreCase));
    }

    // The props imported at evaluation still list the removed file; the compiler mustn't get it.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void A_type_gone_from_the_DAR_is_gone_from_the_build_and_the_disk()
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        var first = Build(MSBuildHost.Dotnet);
        var give = Listed().Single(f => f.EndsWith("Asset.Give.cs", StringComparison.Ordinal));
        Scenario(Generator("Finance", give: false));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.1");

        var second = Build(MSBuildHost.Dotnet, "Build;ShowCompiled");

        first.ExitCode.ShouldBe(0, first.Log);
        second.ExitCode.ShouldBe(0, second.Log);
        second.Log.ShouldNotContain("CS2001");
        Lines(second.Log, "Compiled: ").ShouldNotContain(give);
        Listed().Select(Path.GetFileName).ShouldBe(["Asset.cs"]);
        File.Exists(give).ShouldBeFalse();
    }

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void A_withdrawn_reference_leaves_no_generated_code_behind()
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        var first = Build(MSBuildHost.MSBuildExe);
        App("");

        var second = Build(MSBuildHost.MSBuildExe, "Build;ShowCompiled");

        first.ExitCode.ShouldBe(0, first.Log);
        second.ExitCode.ShouldBe(0, second.Log);
        Lines(second.Log, "Compiled: ").ShouldNotContain(f => f.StartsWith(GeneratedRoot, StringComparison.OrdinalIgnoreCase));
        Listed().ShouldBeEmpty();
        (Directory.Exists(GeneratedRoot) ? Directory.GetFileSystemEntries(GeneratedRoot) : []).ShouldBeEmpty();
        GeneratorRuns().ShouldBe(1);
    }

    // In the project folder, where the SDK's globs would find the generated files a second time.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void Source_mode_compiles_each_generated_file_once_next_to_the_hand_written_ones()
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""", "<DamlGeneratedOutputMode>Source</DamlGeneratedOutputMode>");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");

        var first = Build(MSBuildHost.Dotnet, "Build;ShowCompiled");
        var second = Build(MSBuildHost.Dotnet, "Build;ShowCompiled");

        foreach (var run in new[] { first, second })
        {
            run.ExitCode.ShouldBe(0, run.Log);
            run.Log.ShouldNotContain("CS2002");
            var compiled = Lines(run.Log, "Compiled: ");
            compiled.ShouldBeUnique();
            compiled.ShouldContain(Path.Combine(AppFolder, "Uses.cs"));
            compiled.Count(f => f.StartsWith(Path.Combine(AppFolder, @"Generated\Daml\"), StringComparison.OrdinalIgnoreCase)).ShouldBe(2);
        }
        XDocument.Load(Props).Root!.Descendants("Compile").ShouldAllBe(c => c.Attribute("Visible") == null);
        GeneratorRuns().ShouldBe(1);
    }

    // One generated folder in the sources can't serve two target frameworks.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void Source_mode_with_two_target_frameworks_stops_before_the_generator()
    {
        App(
            """<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""",
            "<DamlGeneratedOutputMode>Source</DamlGeneratedOutputMode>",
            "<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");

        var run = Build(MSBuildHost.MSBuildExe);

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain("error DAML0108");
        GeneratorRuns().ShouldBe(0);
    }

    // Another generator is another generation, even for the DAR it already turned into C#.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void Another_generator_pin_regenerates_the_same_DAR_and_drops_the_old_generation()
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");
        var first = Build(MSBuildHost.Dotnet);
        var old = Directory.GetDirectories(GeneratedRoot).ShouldHaveSingleItem();
        var next = SyntheticGeneratorCache.Create(Path.Combine(_localAppData, @"DamlVisualStudio\codegen"), variant: "next release");
        new GeneratorCache(next.Root, next.Pin).MarkReady();

        var second = Build(MSBuildHost.Dotnet, properties: ("DamlCodegenComponent", next.Pin.Reference));

        first.ExitCode.ShouldBe(0, first.Log);
        second.ExitCode.ShouldBe(0, second.Log);
        GeneratorRuns().ShouldBe(2);
        var generation = Directory.GetDirectories(GeneratedRoot).ShouldHaveSingleItem();
        generation.ShouldNotBe(old);
        Listed().ShouldAllBe(f => f.StartsWith(generation + @"\", StringComparison.OrdinalIgnoreCase) && File.Exists(f));
    }

    // Generated code that isn't what the generator wrote any more is generated again, not compiled as found.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData("deleted file")]
    [InlineData("edited file")]
    [InlineData("deleted generation")]
    public void Damaged_generated_code_is_generated_again_by_the_next_build(string damage)
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");
        var first = Build(MSBuildHost.MSBuildExe);
        var give = Listed().Single(f => f.EndsWith("Asset.Give.cs", StringComparison.Ordinal));
        var original = File.ReadAllText(give);
        switch (damage)
        {
            case "deleted file":
                File.Delete(give);
                break;
            case "edited file":
                File.WriteAllText(give, original.Replace("\"give\"", "\"edited\""));
                break;
            default:
                Directory.Delete(Directory.GetDirectories(GeneratedRoot).Single(), recursive: true);
                break;
        }

        var second = Build(MSBuildHost.MSBuildExe);

        first.ExitCode.ShouldBe(0, first.Log);
        second.ExitCode.ShouldBe(0, second.Log);
        second.Log.ShouldContain("Generated C# for");
        GeneratorRuns().ShouldBe(2);
        File.ReadAllText(give).ShouldBe(original);
        File.Exists(Stamp).ShouldBeTrue();
    }

    // The generation folder is named after the reference, not only the DAR's file name.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void Two_DARs_with_one_file_name_generate_side_by_side()
    {
        App("""
            <DamlDar Include="..\north\finance.dar" Namespace="North" />
            <DamlDar Include="..\south\finance.dar" Namespace="South" />
            """);
        Scenario(Generator("North"), Generator("South"));
        Dar(@"north\finance.dar", "finance-north");
        Dar(@"south\finance.dar", "finance-south");
        Uses("North");
        _sandbox.Write(@"App\UsesSouth.cs", "internal static class UsesSouth\n{\n    public const string Give = South.Asset.Asset.Give;\n}\n");

        var first = Build(MSBuildHost.Dotnet);
        var second = Build(MSBuildHost.Dotnet);

        first.ExitCode.ShouldBe(0, first.Log);
        Directory.GetDirectories(GeneratedRoot).Length.ShouldBe(2);
        Listed().Select(f => f.Substring(GeneratedRoot.Length + 1).Split('\\')[1]).Distinct().ShouldBe(["North", "South"], ignoreOrder: true);
        second.ExitCode.ShouldBe(0, second.Log);
        GeneratorRuns().ShouldBe(2);
    }

    // Each target framework gets a generation of its own in its intermediate folder; the next build runs nothing.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_multi_targeted_project_generates_once_per_target_framework(MSBuildHost host)
    {
        App(
            """<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""",
            frameworks: "<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");

        var first = Build(host);
        var second = Build(host);

        first.ExitCode.ShouldBe(0, first.Log);
        foreach (var framework in new[] { "net10.0", "net10.0-windows" })
        {
            var root = Path.Combine(AppFolder, @"obj\Debug", framework, "Daml");
            var listed = XDocument.Load(Path.Combine(AppFolder, $@"obj\daml\App.csproj.Debug.{framework}.g.props")).Root!
                .Descendants("Compile").Select(c => (string)c.Attribute("Include")!).ToList();
            listed.Select(Path.GetFileName).ShouldBe(["Asset.cs", "Asset.Give.cs"], ignoreOrder: true);
            listed.ShouldAllBe(f => f.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase) && File.Exists(f));
            File.Exists(Path.Combine(AppFolder, @"bin\Debug", framework, "App.dll")).ShouldBeTrue();
        }
        second.ExitCode.ShouldBe(0, second.Log);
        GeneratorRuns().ShouldBe(2);
    }

    // A file of the user's own among the generated ones survives everything the build does there. A build
    // that would generate into its folder stops with DAML0108 naming it; removing the generated code
    // keeps it and says so.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void Source_mode_never_deletes_a_file_of_the_users_own()
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""", "<DamlGeneratedOutputMode>Source</DamlGeneratedOutputMode>");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        var first = Build(MSBuildHost.Dotnet);
        var asset = Listed().Single(f => f.EndsWith(@"\Asset.cs", StringComparison.Ordinal));
        var mine = Path.Combine(Path.GetDirectoryName(asset)!, "Asset.Mine.cs");
        const string Text = "namespace Finance.Asset { public sealed partial class Asset { public const string Mine = \"mine\"; } }\n";
        File.WriteAllText(mine, Text);
        const string Stopped = @"has files the generator didn't write: Finance\Asset\Asset.Mine.cs";
        const string Kept = @"but kept what the generator didn't write there: Finance\Asset\Asset.Mine.cs";
        var steps = new List<(string Name, MSBuildRun Run, int ExitCode, string Says)>();

        steps.Add(("rebuild", Build(MSBuildHost.Dotnet, "Rebuild"), 1, "error DAML0108"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.1");
        steps.Add(("new DAR", Build(MSBuildHost.Dotnet), 1, Stopped));
        App("", "<DamlGeneratedOutputMode>Source</DamlGeneratedOutputMode>");
        steps.Add(("reference removed", Build(MSBuildHost.Dotnet), 0, Kept));
        steps.Add(("clean", Build(MSBuildHost.Dotnet, "Clean"), 0, Kept));

        first.ExitCode.ShouldBe(0, first.Log);
        steps[0].Run.Log.ShouldContain(Stopped);
        foreach (var (name, run, exitCode, says) in steps)
        {
            run.ExitCode.ShouldBe(exitCode, name + "\n" + run.Log);
            run.Log.ShouldContain(says, Case.Sensitive, name);
            File.ReadAllText(mine).ShouldBe(Text, name);
        }
    }

    // Background sync's refresh and a build of the same project at once: one of them generates, the other
    // takes what it published, and the next build has nothing left to generate.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public async Task A_refresh_and_a_build_at_once_generate_once(MSBuildHost host)
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        var generator = Generator("Finance");
        generator.DelayMilliseconds = 1500;
        Scenario(generator);
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");
        var restore = Build(host, "Restore", restore: false);

        var runs = await Task.WhenAll(
            Task.Run(() => Build(host, restore: false)),
            Task.Run(() => Build(host, "DamlRefreshBindings", restore: false, ("BuildProjectReferences", "false"))));
        var next = Build(host, restore: false);

        restore.ExitCode.ShouldBe(0, restore.Log);
        runs.ShouldAllBe(r => r.ExitCode == 0, string.Join("\n----\n", runs.Select(r => r.Log)));
        next.ExitCode.ShouldBe(0, next.Log);
        GeneratorRuns().ShouldBe(1);
        Directory.GetDirectories(GeneratedRoot).ShouldHaveSingleItem();
        Listed().ShouldAllBe(f => File.Exists(f));
    }

    // A clean while a refresh generates from a new DAR waits for it. Whichever goes second, the folder
    // holds a whole generation or none, never a half-published one, and the next build compiles.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task A_clean_and_a_refresh_at_once_leave_no_half_generation()
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");
        var first = Build(MSBuildHost.Dotnet);
        var slow = Generator("Finance");
        slow.DelayMilliseconds = 1500;
        Scenario(slow);
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.1");

        var runs = await Task.WhenAll(
            Task.Run(() => Build(MSBuildHost.Dotnet, "DamlRefreshBindings", restore: false, ("BuildProjectReferences", "false"))),
            Task.Run(() => Build(MSBuildHost.Dotnet, "Clean", restore: false)));
        var left = Directory.Exists(GeneratedRoot) ? Directory.GetFileSystemEntries(GeneratedRoot) : [];
        var whole = left.All(g => File.Exists(Path.Combine(g, GenerationManifest.FileName)) && Directory.GetFiles(g, "*.cs", SearchOption.AllDirectories).Length == 2);
        var next = Build(MSBuildHost.Dotnet, restore: false);

        first.ExitCode.ShouldBe(0, first.Log);
        runs.ShouldAllBe(r => r.ExitCode == 0, string.Join("\n----\n", runs.Select(r => r.Log)));
        left.Length.ShouldBeLessThanOrEqualTo(1, string.Join(", ", left));
        whole.ShouldBeTrue(string.Join(", ", left));
        next.ExitCode.ShouldBe(0, next.Log);
        Directory.GetDirectories(GeneratedRoot).ShouldHaveSingleItem();
        Listed().ShouldAllBe(f => File.Exists(f));
        File.Exists(Path.Combine(AppFolder, @"bin\Debug\net10.0\App.dll")).ShouldBeTrue();
    }

    // Two projects pinned to different generator releases build at once, each generating with the private
    // dpm home of its own pin.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task Two_projects_on_different_generator_pins_build_at_once_each_in_its_own_home()
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        var generator = Generator("Finance");
        generator.DelayMilliseconds = 1500;
        Scenario(generator);
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");
        var root = Path.Combine(_localAppData, @"DamlVisualStudio\codegen");
        var other = SyntheticGeneratorCache.Create(root, variant: "next release");
        new GeneratorCache(other.Root, other.Pin).MarkReady();
        _sandbox.Write(@"Other\Other.csproj", File.ReadAllText(Path.Combine(AppFolder, "App.csproj")));
        _sandbox.Write(@"Other\Uses.cs", File.ReadAllText(Path.Combine(AppFolder, "Uses.cs")));

        var runs = await Task.WhenAll(
            Task.Run(() => Build(MSBuildHost.MSBuildExe)),
            Task.Run(() => BuildProject(@"Other\Other.csproj", MSBuildHost.MSBuildExe, properties: ("DamlCodegenComponent", other.Pin.Reference))));

        runs.ShouldAllBe(r => r.ExitCode == 0, string.Join("\n----\n", runs.Select(r => r.Log)));
        _dpm.ReadCalls().Where(c => c.Args.Count > 0 && c.Args[0] == "codegen-cs").Select(c => c.Environment["DPM_HOME"])
            .ShouldBe([new GeneratorCache(root, _pin!).Home, other.Home], ignoreOrder: true);
    }

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void Two_DARs_generating_the_same_types_fail_with_DAML0202()
    {
        App("""
            <DamlDar Include="..\vendor\finance-1.0.0.dar" />
            <DamlDar Include="..\vendor\treasury-1.0.0.dar" />
            """);
        Scenario(Generator(null));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Dar(@"vendor\treasury-1.0.0.dar", "treasury-1.0.0");

        var run = Build(MSBuildHost.Dotnet);

        run.ExitCode.ShouldBe(1, run.Log);
        run.Log.ShouldContain("error DAML0202");
    }

    // Design-time builds run nothing: before the first build they say so, after it they give the
    // compiler what the last build published, and evaluation alone already lists it for IntelliSense.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void Design_time_builds_run_no_tool_and_show_what_the_last_build_generated(MSBuildHost host)
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Scenario(Generator("Finance"));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");

        var restore = Build(host, "Restore", restore: false);
        var before = DesignTime(host);
        var emptyList = Listed();
        var noOutput = !Directory.Exists(GeneratedRoot);
        var build = Build(host);
        var after = DesignTime(host);
        var evaluated = Build(host, "ShowEvaluated", restore: false, ("DesignTimeBuild", "true"));

        restore.ExitCode.ShouldBe(0, restore.Log);
        before.ExitCode.ShouldBe(0, before.Log);
        before.Log.ShouldContain("build the project to generate it");
        emptyList.ShouldBeEmpty();
        noOutput.ShouldBeTrue();
        build.ExitCode.ShouldBe(0, build.Log);
        after.ExitCode.ShouldBe(0, after.Log);
        var generated = Listed();
        Lines(after.Log, "Compiled: ").Where(f => f.StartsWith(GeneratedRoot, StringComparison.OrdinalIgnoreCase)).ShouldBe(generated, ignoreOrder: true);
        evaluated.ExitCode.ShouldBe(0, evaluated.Log);
        Lines(evaluated.Log, "Evaluated: ").ShouldBe(generated, ignoreOrder: true);
        GeneratorRuns().ShouldBe(1);
    }

    // Long namespaces make long paths. The compiler of the .NET SDK reads them whatever Windows'
    // long path setting is, so they stay a warning. The 32-bit MSBuild.exe can't reach them at all.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void Generated_files_past_260_characters_compile_with_a_DAML0111_warning()
    {
        var @namespace = "Finance." + string.Join(".", Enumerable.Repeat("Settlement", 13));
        App($"""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="{@namespace}" />""");
        Scenario(Generator(@namespace));
        Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses(@namespace);

        var first = Build(MSBuildHost.Dotnet, "Build;ShowCompiled");
        var second = Build(MSBuildHost.Dotnet, "Build;ShowCompiled");

        first.ExitCode.ShouldBe(0, first.Log);
        first.Log.ShouldContain("warning DAML0111");
        Lines(first.Log, "Compiled: ").Max(f => f.Length).ShouldBeGreaterThan(259);
        File.Exists(Path.Combine(AppFolder, @"bin\Debug\net10.0\App.dll")).ShouldBeTrue();
        second.ExitCode.ShouldBe(0, second.Log);
        GeneratorRuns().ShouldBe(1);
    }

    // The list is emptied rather than deleted: Visual Studio watches only the files a project's
    // evaluation imported, and would miss the list the next build writes until a reload.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void Clean_removes_the_generated_code_empties_its_list_and_leaves_the_rest()
    {
        App("""<DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />""");
        Scenario(Generator("Finance"));
        var dar = Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        Uses("Finance");
        var build = Build(MSBuildHost.Dotnet);

        var clean = Build(MSBuildHost.Dotnet, "Clean");

        build.ExitCode.ShouldBe(0, build.Log);
        clean.ExitCode.ShouldBe(0, clean.Log);
        Directory.Exists(GeneratedRoot).ShouldBeFalse();
        XDocument.Load(Props).Root!.Descendants("Compile").ShouldBeEmpty();
        File.Exists(Stamp).ShouldBeFalse();
        File.Exists(Path.Combine(AppFolder, "Uses.cs")).ShouldBeTrue();
        File.Exists(dar).ShouldBeTrue();
    }

    // What Visual Studio's fast up-to-date check asks for, through its own targets: the DARs and
    // the package's build files in, the stamp of the last real build out, the defaults kept.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public void The_up_to_date_check_gets_the_DARs_and_the_stamp_and_keeps_its_own_targets()
    {
        App("""
            <DamlReference Include="..\contracts\Contracts.msbuildproj" Namespace="Contracts" />
            <DamlDar Include="..\vendor\finance-1.0.0.dar" Namespace="Finance" />
            """);
        Contracts();
        Scenario(BuildsTheDar(), Generator("Contracts"), Generator("Finance"));
        var dar = Dar(@"vendor\finance-1.0.0.dar", "finance-1.0.0");
        var build = Build(MSBuildHost.MSBuildExe);

        var check = DesignTime(MSBuildHost.MSBuildExe, "ShowUpToDateCheck");

        build.ExitCode.ShouldBe(0, build.Log);
        check.ExitCode.ShouldBe(0, check.Log);
        Lines(check.Log, "Input targets: ").ShouldBe(["CompileDesignTime;ResolveCodeAnalysisRuleSet;DamlCollectUpToDateCheckItems"]);
        Lines(check.Log, "Built targets: ").ShouldBe(["CompileDesignTime;DamlCollectUpToDateCheckItems"]);
        var inputs = Lines(check.Log, "Input: ");
        inputs.ShouldContain(Path.Combine(_sandbox.Root, @"contracts\.daml\dist\contracts-1.2.0.dar"));
        inputs.ShouldContain(dar);
        inputs.ShouldContain(f => f.EndsWith(@"\build\Daml.Build.CSharp.targets", StringComparison.OrdinalIgnoreCase));
        inputs.ShouldContain(f => f.EndsWith(@"\build\Daml.Build.CSharp.props", StringComparison.OrdinalIgnoreCase));
        Lines(check.Log, "Built: ").ShouldContain(Stamp);
        GeneratorRuns().ShouldBe(2);
    }

    // The manual check's solution, restored and built for real on each host: dpm builds the DAR, the
    // pinned generator turns it into C#, the compiler takes it with Daml.Runtime; the next build
    // runs neither tool.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    [InlineData(MSBuildHost.MSBuildExe)]
    [InlineData(MSBuildHost.Dotnet)]
    public void A_real_Daml_project_turns_into_compiled_CSharp_and_the_next_build_runs_no_tool(MSBuildHost host)
    {
        var hostPath = HostPath(host);
        var properties = RealProjects();

        var first = _sandbox.Build(host, hostPath, @"App\App.csproj", properties: properties, target: "Build;ShowCompiled");
        var second = _sandbox.Build(host, hostPath, @"App\App.csproj", properties: properties, target: "Build;ShowCompiled");

        first.ExitCode.ShouldBe(0, first.Log);
        first.Log.ShouldContain("Generated C# for");
        var generated = Lines(first.Log, "Compiled: ").Where(f => f.StartsWith(GeneratedRoot, StringComparison.OrdinalIgnoreCase)).ToList();
        generated.ShouldNotBeEmpty();
        generated.ShouldAllBe(f => File.Exists(f));
        File.Exists(Path.Combine(AppFolder, @"bin\Debug\net10.0\App.dll")).ShouldBeTrue();
        second.ExitCode.ShouldBe(0, second.Log);
        second.Log.ShouldNotContain("Generated C# for");
        second.Log.ShouldNotContain("Compiling contracts");
        Lines(second.Log, "Compiled: ").Where(f => f.StartsWith(GeneratedRoot, StringComparison.OrdinalIgnoreCase)).ShouldBe(generated);
        _output.WriteLine($"{host}: first build {first.Elapsed.TotalSeconds:F1} s, second {second.Elapsed.TotalSeconds:F1} s.");
    }

    // dpm's own home belongs to the user: building, generating and cleaning leave every file in it as they
    // found it, because the generator runs from the private home of its pin.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    public void A_real_build_and_clean_leave_the_dpm_home_as_they_found_it()
    {
        var hostPath = HostPath(MSBuildHost.Dotnet);
        var properties = RealProjects();
        var home = Path.GetDirectoryName(Path.GetDirectoryName(properties["DamlDpmPath"]))!;
        var before = Snapshot(home);

        var build = _sandbox.Build(MSBuildHost.Dotnet, hostPath, @"App\App.csproj", properties: properties);
        var clean = _sandbox.Build(MSBuildHost.Dotnet, hostPath, @"App\App.csproj", properties: properties, target: "Clean");

        build.ExitCode.ShouldBe(0, build.Log);
        build.Log.ShouldContain("Generated C# for");
        clean.ExitCode.ShouldBe(0, clean.Log);
        Directory.Exists(GeneratedRoot).ShouldBeFalse();
        before.ShouldNotBeEmpty();
        Snapshot(home).ShouldBe(before);
    }

    // Every file under the folder with its size and time of its last write.
    private static List<string> Snapshot(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => $"{f.FullName.Substring(folder.Length)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // The manual check's Daml project and a C# project on its bindings, for the real dpm and generator.
    private Dictionary<string, string> RealProjects()
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        _sandbox.UseSdkFeed();
        var fixture = Path.Combine(Repository.Root, @"test\fixtures\sdk-manual\Contracts");
        _sandbox.Write(@"contracts\daml.yaml", File.ReadAllText(Path.Combine(fixture, "daml.yaml")));
        _sandbox.Write(@"contracts\daml\Asset.daml", File.ReadAllText(Path.Combine(fixture, @"daml\Asset.daml")));
        _sandbox.Write(@"contracts\Contracts.msbuildproj", $"<Project Sdk=\"Daml.Sdk/{SdkFeed.Version}\" />\n");
        _sandbox.Write(@"App\App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {SingleFramework}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Daml.Build.CSharp" Version="{SdkFeed.Version}" PrivateAssets="all" />
                <PackageReference Include="Daml.Runtime" Version="{SdkFeed.RuntimeVersion}" />
                <PackageReference Include="Daml.Ledger.Abstractions" Version="{SdkFeed.RuntimeVersion}" />
                <DamlReference Include="..\contracts\Contracts.msbuildproj" Namespace="SdkManual" />
              </ItemGroup>
            {ShowTargets}
            </Project>
            """);
        return new Dictionary<string, string>
        {
            ["DamlDpmPath"] = dpm,
            ["DamlJavaHome"] = Path.GetDirectoryName(Path.GetDirectoryName(java))!,
            // Installs the generator once on a machine that hasn't got it yet.
            ["DamlAllowToolInstall"] = "true",
        };
    }
}
