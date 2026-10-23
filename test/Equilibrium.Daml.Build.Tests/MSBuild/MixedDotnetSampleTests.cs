using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Equilibrium.Daml.Build.Tests.MSBuild;

// samples\mixed-dotnet as a user gets it: Daml contracts with Splice data-dependencies and their Script
// tests as Equilibrium.Daml.Sdk projects, and a C# app on the C# generated from the contracts, in one solution.
public sealed class MixedDotnetSampleTests : IDisposable
{
    private static readonly string Sample = Repository.Sample("mixed-dotnet");

    // Made by the first test that needs it: the package-reading test needs no built tasks.
    private readonly Lazy<MSBuildSandbox> _sandbox = new(() => new MSBuildSandbox(shortRoot: true));
    private readonly ITestOutputHelper _output;

    public MixedDotnetSampleTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (_sandbox.IsValueCreated)
        {
            _sandbox.Value.Dispose();
        }
    }

    private static string HostPath(MSBuildHost host) =>
        RequiredTools.Require(host == MSBuildHost.MSBuildExe ? ExternalTool.MSBuild : ExternalTool.Dotnet);

    // What scripts/pack.ps1 makes on a developer's machine, where the build number is 0.
    private static string LocalPackageVersion()
    {
        var properties = XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props")).Descendants();
        var prefix = properties.Single(p => p.Name.LocalName == "VersionPrefix").Value;
        var label = properties.Single(p => p.Name.LocalName == "EqDamlPrereleaseLabel").Value;
        return label.Length > 0 ? $"{prefix}-{label}.0" : prefix;
    }

    private static string PackageVersion(XDocument project, string package) =>
        (string)project.Descendants("PackageReference").Single(r => (string?)r.Attribute("Include") == package).Attribute("Version")!;

    // The sample restores from artifacts\packages, so it has to name the packages a local pack puts there.
    [Fact, Trait("Category", "Unit")]
    public void The_sample_uses_the_packages_a_local_pack_makes()
    {
        var app = XDocument.Load(Path.Combine(Sample, "App", "App.csproj"));

        foreach (var project in Directory.GetFiles(Sample, "*.msbuildproj", SearchOption.AllDirectories))
        {
            XDocument.Load(project).Root!.Attribute("Sdk")!.Value.ShouldBe($"Equilibrium.Daml.Sdk/{LocalPackageVersion()}", project);
        }
        PackageVersion(app, "Equilibrium.Daml.Build.CSharp").ShouldBe(LocalPackageVersion());
        PackageVersion(app, "Daml.Runtime").ShouldBe(ReleaseTools.RuntimeVersion);
        PackageVersion(app, "Daml.Ledger.Abstractions").ShouldBe(ReleaseTools.RuntimeVersion);
        PackageVersion(app, "Splice.Api.Token.Holding.V2").ShouldBe(SdkFeed.SpliceVersion);
        PackageVersion(app, "Splice.Api.Token.Transfer.Instruction.V2").ShouldBe(SdkFeed.SpliceVersion);
    }

    // A copy without anything a build of the sample in place left, on the packages of this test run and the
    // given Daml SDK.
    private void CopySample(string sdk)
    {
        string[] skipped = [".daml", "obj", "bin", "log"];
        foreach (var file in Directory.GetFiles(Sample, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(Sample.Length + 1);
            if (relative.Split('\\').Any(s => skipped.Contains(s, StringComparer.OrdinalIgnoreCase)) || relative.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var target = Path.Combine(_sandbox.Value.Root, "mixed-dotnet", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        foreach (var project in Directory.GetFiles(Path.Combine(_sandbox.Value.Root, "mixed-dotnet"), "*.*proj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(project);
            text = Regex.Replace(text, @"Equilibrium\.Daml\.Sdk/[^""]+", $"Equilibrium.Daml.Sdk/{SdkFeed.Version}");
            text = Regex.Replace(text, @"Include=""Equilibrium\.Daml\.Build\.CSharp"" Version=""[^""]+""", $"Include=\"Equilibrium.Daml.Build.CSharp\" Version=\"{SdkFeed.Version}\"");
            File.WriteAllText(project, text);
        }
        foreach (var manifest in Directory.GetFiles(Path.Combine(_sandbox.Value.Root, "mixed-dotnet"), "*.yaml", SearchOption.AllDirectories))
        {
            File.WriteAllText(manifest, Regex.Replace(File.ReadAllText(manifest), @"^sdk-version: .*$", $"sdk-version: {sdk}", RegexOptions.Multiline));
        }
        _sandbox.Value.UseSdkFeed();
    }

    // dpm builds the contracts against the Splice DARs and then their tests, the pinned generator turns the
    // contracts into C# that the app compiles with the Splice packages and runs, and the scripts pass; on
    // both supported SDK lines.
    [SkippableTheory, Trait("Category", "MSBuild"), Trait("Backend", "Real"), Trait("RequiresDaml", "true"), Trait("RequiresJava", "true")]
    [InlineData(MSBuildHost.MSBuildExe, "3.6.0")]
    [InlineData(MSBuildHost.MSBuildExe, "3.5.2")]
    [InlineData(MSBuildHost.Dotnet, "3.6.0")]
    [InlineData(MSBuildHost.Dotnet, "3.5.2")]
    public async Task The_sample_solution_builds_runs_and_passes_its_Daml_tests(MSBuildHost host, string sdk)
    {
        using var alone = await DpmHome.ExclusiveAsync();
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var java = RequiredTools.Require(ExternalTool.Java);
        var dotnet = RequiredTools.Require(ExternalTool.Dotnet);
        var hostPath = HostPath(host);
        CopySample(sdk);
        var properties = new Dictionary<string, string>
        {
            ["EqDamlDpmPath"] = dpm,
            ["EqDamlJavaHome"] = Path.GetDirectoryName(Path.GetDirectoryName(java))!,
            // Installs the generator once on a machine that hasn't got it yet.
            ["EqDamlAllowToolInstall"] = "true",
        };

        var build = _sandbox.Value.Build(host, hostPath, @"mixed-dotnet\MixedDotnet.slnx", properties: properties);
        build.ExitCode.ShouldBe(0, build.Log);
        build.Log.ShouldContain("Generated C# for");
        DarProvenance.Read(Path.Combine(_sandbox.Value.Root, @"mixed-dotnet\daml\.daml\dist\mixed-dotnet-contracts-0.1.0.dar")).SdkVersion.ShouldBe(sdk);
        var app = await new ProcessRunner().RunAsync(
            new ProcessSpec(dotnet, [Path.Combine(_sandbox.Value.Root, @"mixed-dotnet\App\bin\Debug\net10.0\App.dll")]) { Timeout = TimeSpan.FromMinutes(1) },
            CancellationToken.None);
        app.ExitCode.ShouldBe(0, app.Stdout + app.Stderr);
        app.Stdout.Trim().ShouldBe("Gold from Daml package mixed-dotnet-contracts 0.1.0");

        var tests = _sandbox.Value.Build(host, hostPath, @"mixed-dotnet\daml\test\Contracts.Tests.msbuildproj", properties: properties, target: "EqDamlTest");
        tests.ExitCode.ShouldBe(0, tests.Log);
        _output.WriteLine($"{host}, SDK {sdk}: build {build.Elapsed.TotalSeconds:F1} s, tests {tests.Elapsed.TotalSeconds:F1} s.");
    }
}
