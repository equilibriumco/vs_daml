using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.MSBuild;

// The Equilibrium.Daml.Sdk package as dotnet pack makes it from the built solution.
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
        ["tasks/netstandard2.0/Equilibrium.Daml.Build.Core.dll", "tasks/netstandard2.0/Equilibrium.Daml.Build.Tasks.dll", "tasks/netstandard2.0/YamlDotNet.dll", "NOTICE"];

    private const string RepositoryUrl = "https://github.com/equilibriumco/vs_daml";

    private static void ShouldBeApacheLicensed(XElement metadata)
    {
        var license = metadata.Elements().Single(e => e.Name.LocalName == "license");
        ((string?)license.Attribute("type")).ShouldBe("expression");
        license.Value.ShouldBe("Apache-2.0");
    }

    // nuget.org shows the readme on the package page and links the repository at the commit that
    // was packed.
    private static void ShouldDescribeItself(XElement metadata)
    {
        metadata.Elements().Single(e => e.Name.LocalName == "readme").Value.ShouldBe("README.md");
        metadata.Elements().Single(e => e.Name.LocalName == "projectUrl").Value.ShouldBe(RepositoryUrl);
        var repository = metadata.Elements().Single(e => e.Name.LocalName == "repository");
        ((string?)repository.Attribute("type")).ShouldBe("git");
        ((string?)repository.Attribute("url")).ShouldBe(RepositoryUrl);
        ((string?)repository.Attribute("commit") ?? "").ShouldMatch("^[0-9a-f]{40}$");
    }

    // From what the solution build left, so nothing is rebuilt under tests that read it, and with
    // the nuspec kept out of obj, where a run for the other framework may be packing too.
    private async Task<(string[] Entries, XElement Metadata)> PackAsync(string project, string id)
    {
        var pack = await new ProcessRunner().RunAsync(
            new ProcessSpec(RequiredTools.Require(ExternalTool.Dotnet), ["pack", Path.Combine(Repository.Root, project), "--no-build", "-c", Configuration, "-o", _output, $"-p:NuspecOutputPath={Path.Combine(_output, "nuspec")}", $"-p:_EqDamlStampedTemplates={Path.Combine(_output, "templates")}", "-nologo"])
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
        var (entries, metadata) = await PackAsync(@"src\Equilibrium.Daml.Sdk\Equilibrium.Daml.Sdk.csproj", "Equilibrium.Daml.Sdk");

        entries.ShouldBe(["Equilibrium.Daml.Sdk.nuspec", "README.md", "Sdk/Sdk.props", "Sdk/Sdk.targets", .. Tasks], ignoreOrder: true);
        metadata.Descendants().Where(e => e.Name.LocalName == "packageType").Select(e => (string?)e.Attribute("name")).ShouldBe(["MSBuildSdk"]);
        metadata.Descendants().Where(e => e.Name.LocalName == "dependency").ShouldBeEmpty();
        ShouldBeApacheLicensed(metadata);
        ShouldDescribeItself(metadata);
    }

    // No symbol package: the tasks aren't in lib\, so each assembly carries its own PDB, with links
    // to its sources in the repository at the packed commit.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task The_task_assemblies_carry_their_symbols_and_source_links()
    {
        await PackAsync(@"src\Equilibrium.Daml.Sdk\Equilibrium.Daml.Sdk.csproj", "Equilibrium.Daml.Sdk");

        using var package = ZipFile.OpenRead(Directory.GetFiles(_output, "Equilibrium.Daml.Sdk.*.nupkg").ShouldHaveSingleItem());
        foreach (var assembly in new[] { "Equilibrium.Daml.Build.Core.dll", "Equilibrium.Daml.Build.Tasks.dll" })
        {
            var sourceLink = SourceLinkOf(package.GetEntry("tasks/netstandard2.0/" + assembly)!);
            sourceLink.ShouldNotBeNull(assembly + " has no embedded PDB with a source link");
            sourceLink.ShouldContain("https://raw.githubusercontent.com/equilibriumco/vs_daml/", Case.Sensitive, assembly);
        }
    }

    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    private static string? SourceLinkOf(ZipArchiveEntry entry)
    {
        using var copy = new MemoryStream();
        using (var stream = entry.Open())
        {
            stream.CopyTo(copy);
        }
        copy.Position = 0;
        using var pe = new System.Reflection.PortableExecutable.PEReader(copy);
        var embedded = pe.ReadDebugDirectory().Where(d => d.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
        if (embedded.Length == 0)
        {
            return null;
        }
        using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0]);
        var pdb = provider.GetMetadataReader();
        return pdb.CustomDebugInformation
            .Select(pdb.GetCustomDebugInformation)
            .Where(info => pdb.GetGuid(info.Kind) == SourceLinkKind)
            .Select(info => System.Text.Encoding.UTF8.GetString(pdb.GetBlobBytes(info.Value)))
            .FirstOrDefault();
    }

    // build\, not buildTransitive\: the package acts on the project that references it, and never
    // flows on to the projects that reference that one.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task The_CSharp_package_is_a_development_dependency_with_build_files_named_after_it()
    {
        var (entries, metadata) = await PackAsync(@"src\Equilibrium.Daml.Build.CSharp\Equilibrium.Daml.Build.CSharp.csproj", "Equilibrium.Daml.Build.CSharp");

        entries.ShouldBe(["Equilibrium.Daml.Build.CSharp.nuspec", "README.md", "build/Equilibrium.Daml.Build.CSharp.props", "build/Equilibrium.Daml.Build.CSharp.targets", .. Tasks], ignoreOrder: true);
        metadata.Elements().Single(e => e.Name.LocalName == "developmentDependency").Value.ShouldBe("true");
        metadata.Descendants().Where(e => e.Name.LocalName == "dependency").ShouldBeEmpty();
        ShouldBeApacheLicensed(metadata);
        ShouldDescribeItself(metadata);
    }

    // dotnet new finds each template under content\, and each template.json names the release that
    // packed it, for the projects it creates to use.
    [SkippableFact, Trait("Category", "MSBuild"), Trait("Backend", "Fake")]
    public async Task The_templates_package_holds_both_templates_with_its_own_release_written_in()
    {
        var (entries, metadata) = await PackAsync(@"src\Equilibrium.Daml.Templates\Equilibrium.Daml.Templates.csproj", "Equilibrium.Daml.Templates");

        var content = Path.Combine(Repository.Root, @"src\Equilibrium.Daml.Templates\content");
        var files = Directory.GetFiles(content, "*", SearchOption.AllDirectories).Select(f => "content/" + f.Substring(content.Length + 1).Replace('\\', '/'));
        entries.ShouldBe(["Equilibrium.Daml.Templates.nuspec", "README.md", .. files], ignoreOrder: true);
        metadata.Descendants().Where(e => e.Name.LocalName == "packageType").Select(e => (string?)e.Attribute("name")).ShouldBe(["Template"]);
        metadata.Descendants().Where(e => e.Name.LocalName == "dependency").ShouldBeEmpty();
        ShouldBeApacheLicensed(metadata);
        ShouldDescribeItself(metadata);
        var version = metadata.Elements().Single(e => e.Name.LocalName == "version").Value;
        using var package = ZipFile.OpenRead(Directory.GetFiles(_output, "Equilibrium.Daml.Templates.*.nupkg").ShouldHaveSingleItem());
        foreach (var template in new[] { "daml-contracts", "daml-dotnet-app" })
        {
            using var reader = new StreamReader(package.GetEntry($"content/{template}/.template.config/template.json")!.Open());
            var config = await reader.ReadToEndAsync();
            config.ShouldNotContain("@Package");
            config.ShouldContain($"\"value\": \"{version}\"");
            config.ShouldContain("\"value\": \"Equilibrium.Daml\"");
        }
    }
}
