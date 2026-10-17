using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.MSBuild;

// The Daml.Sdk package as dotnet pack makes it from the built solution.
public sealed class DamlSdkPackageTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "daml-sdk-pack-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_output))
        {
            Directory.Delete(_output, recursive: true);
        }
    }

    private static string Configuration =>
        typeof(DamlSdkPackageTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task The_package_is_an_MSBuild_SDK_with_its_props_targets_and_tasks_only()
    {
        var dotnet = RequiredTools.Require(ExternalTool.Dotnet);
        var project = Path.Combine(Repository.Root, @"src\Daml.Sdk\Daml.Sdk.csproj");

        // From what the solution build left, so nothing is rebuilt under tests that read it, and
        // with the nuspec kept out of obj, where a run for the other framework may be packing too.
        var pack = await new ProcessRunner().RunAsync(
            new ProcessSpec(dotnet, ["pack", project, "--no-build", "-c", Configuration, "-o", _output, $"-p:NuspecOutputPath={Path.Combine(_output, "nuspec")}", "-nologo"])
            {
                WorkingDirectory = Repository.Root,
                Timeout = TimeSpan.FromMinutes(5),
            },
            CancellationToken.None);

        pack.ExitCode.ShouldBe(0, pack.Stdout + pack.Stderr);
        using var package = ZipFile.OpenRead(Directory.GetFiles(_output, "Daml.Sdk.*.nupkg").ShouldHaveSingleItem());
        package.Entries.Select(e => e.FullName).Where(n => !n.StartsWith("_rels/", StringComparison.Ordinal) && !n.StartsWith("package/", StringComparison.Ordinal) && n != "[Content_Types].xml")
            .ShouldBe(
            [
                "Daml.Sdk.nuspec",
                "Sdk/Sdk.props",
                "Sdk/Sdk.targets",
                "tasks/netstandard2.0/Daml.Build.Core.dll",
                "tasks/netstandard2.0/Daml.Build.Tasks.dll",
                "tasks/netstandard2.0/YamlDotNet.dll",
            ],
            ignoreOrder: true);
        using var nuspec = package.GetEntry("Daml.Sdk.nuspec")!.Open();
        var metadata = XDocument.Load(nuspec).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
        metadata.Descendants().Where(e => e.Name.LocalName == "packageType").Select(e => (string?)e.Attribute("name")).ShouldBe(["MSBuildSdk"]);
        metadata.Descendants().Where(e => e.Name.LocalName == "dependency").ShouldBeEmpty();
    }
}
