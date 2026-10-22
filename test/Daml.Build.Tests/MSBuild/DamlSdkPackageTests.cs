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

    // NOTICE carries YamlDotNet's MIT notice, which has to go wherever its assembly goes.
    private static readonly string[] Tasks =
        ["tasks/netstandard2.0/Daml.Build.Core.dll", "tasks/netstandard2.0/Daml.Build.Tasks.dll", "tasks/netstandard2.0/YamlDotNet.dll", "NOTICE"];

    private static void ShouldBeApacheLicensed(XElement metadata)
    {
        var license = metadata.Elements().Single(e => e.Name.LocalName == "license");
        ((string?)license.Attribute("type")).ShouldBe("expression");
        license.Value.ShouldBe("Apache-2.0");
    }

    // From what the solution build left, so nothing is rebuilt under tests that read it, and with
    // the nuspec kept out of obj, where a run for the other framework may be packing too.
    private async Task<(string[] Entries, XElement Metadata)> PackAsync(string project, string id)
    {
        var pack = await new ProcessRunner().RunAsync(
            new ProcessSpec(RequiredTools.Require(ExternalTool.Dotnet), ["pack", Path.Combine(Repository.Root, project), "--no-build", "-c", Configuration, "-o", _output, $"-p:NuspecOutputPath={Path.Combine(_output, "nuspec")}", "-nologo"])
            {
                WorkingDirectory = Repository.Root,
                Timeout = TimeSpan.FromMinutes(5),
            },
            CancellationToken.None);
        pack.ExitCode.ShouldBe(0, pack.Stdout + pack.Stderr);

        using var package = ZipFile.OpenRead(Directory.GetFiles(_output, id + ".*.nupkg").ShouldHaveSingleItem());
        var entries = package.Entries.Select(e => e.FullName)
            .Where(n => !n.StartsWith("_rels/", StringComparison.Ordinal) && !n.StartsWith("package/", StringComparison.Ordinal) && n != "[Content_Types].xml")
            .ToArray();
        using var nuspec = package.GetEntry(id + ".nuspec")!.Open();
        return (entries, XDocument.Load(nuspec).Root!.Elements().Single(e => e.Name.LocalName == "metadata"));
    }

    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task The_package_is_an_MSBuild_SDK_with_its_props_targets_and_tasks_only()
    {
        var (entries, metadata) = await PackAsync(@"src\Daml.Sdk\Daml.Sdk.csproj", "Daml.Sdk");

        entries.ShouldBe(["Daml.Sdk.nuspec", "Sdk/Sdk.props", "Sdk/Sdk.targets", .. Tasks], ignoreOrder: true);
        metadata.Descendants().Where(e => e.Name.LocalName == "packageType").Select(e => (string?)e.Attribute("name")).ShouldBe(["MSBuildSdk"]);
        metadata.Descendants().Where(e => e.Name.LocalName == "dependency").ShouldBeEmpty();
        ShouldBeApacheLicensed(metadata);
    }

    // build\, not buildTransitive\: the package acts on the project that references it, and never
    // flows on to the projects that reference that one.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task The_CSharp_package_is_a_development_dependency_with_build_files_named_after_it()
    {
        var (entries, metadata) = await PackAsync(@"src\Daml.Build.CSharp\Daml.Build.CSharp.csproj", "Daml.Build.CSharp");

        entries.ShouldBe(["Daml.Build.CSharp.nuspec", "build/Daml.Build.CSharp.props", "build/Daml.Build.CSharp.targets", .. Tasks], ignoreOrder: true);
        metadata.Elements().Single(e => e.Name.LocalName == "developmentDependency").Value.ShouldBe("true");
        metadata.Descendants().Where(e => e.Name.LocalName == "dependency").ShouldBeEmpty();
        ShouldBeApacheLicensed(metadata);
    }

    // dotnet new finds each template under content\, and each template.json names the release that
    // packed it, for the projects it creates to use.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task The_templates_package_holds_both_templates_with_its_own_release_written_in()
    {
        var (entries, metadata) = await PackAsync(@"src\Daml.Templates\Daml.Templates.csproj", "Daml.Templates");

        var content = Path.Combine(Repository.Root, @"src\Daml.Templates\content");
        var files = Directory.GetFiles(content, "*", SearchOption.AllDirectories).Select(f => "content/" + f.Substring(content.Length + 1).Replace('\\', '/'));
        entries.ShouldBe(["Daml.Templates.nuspec", .. files], ignoreOrder: true);
        metadata.Descendants().Where(e => e.Name.LocalName == "packageType").Select(e => (string?)e.Attribute("name")).ShouldBe(["Template"]);
        metadata.Descendants().Where(e => e.Name.LocalName == "dependency").ShouldBeEmpty();
        ShouldBeApacheLicensed(metadata);
        var version = metadata.Elements().Single(e => e.Name.LocalName == "version").Value;
        using var package = ZipFile.OpenRead(Directory.GetFiles(_output, "Daml.Templates.*.nupkg").ShouldHaveSingleItem());
        foreach (var template in new[] { "daml-contracts", "daml-dotnet-app" })
        {
            using var reader = new StreamReader(package.GetEntry($"content/{template}/.template.config/template.json")!.Open());
            var config = await reader.ReadToEndAsync();
            config.ShouldNotContain("@Package");
            config.ShouldContain($"\"value\": \"{version}\"");
            config.ShouldContain("\"value\": \"Daml\"");
        }
    }
}
