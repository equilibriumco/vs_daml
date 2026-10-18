using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Daml.TestHelpers;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

// The templates the extension brings: the dotnet new package for New Project, which a pkgdef points
// Visual Studio's template engine at, and the item templates for Add New Item in Daml projects.
// Visual Studio says nothing when one is missing or misplaced, so the built VSIX is read here.
public sealed class TemplatesTests : IDisposable
{
    private static readonly XNamespace Vsix = "http://schemas.microsoft.com/developer/vsx-schema/2011";
    private static readonly XNamespace Manifest = "http://schemas.microsoft.com/developer/vstemplatemanifest/2015";
    private static readonly XNamespace Template = "http://schemas.microsoft.com/developer/vstemplate/2005";

    private readonly ZipArchive _vsix = ZipFile.OpenRead(Path.Combine(
        Repository.Root, "src", "Daml.VisualStudio", "bin", Configuration, "net48", "Daml.VisualStudio.vsix"));

    public void Dispose() => _vsix.Dispose();

    private static string Configuration =>
        typeof(TemplatesTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private ZipArchiveEntry Entry(string path) =>
        _vsix.Entries.SingleOrDefault(e => Uri.UnescapeDataString(e.FullName) == path) ?? throw new FileNotFoundException(path);

    private XElement[] Assets(string type) =>
        XDocument.Parse(Read(Entry("extension.vsixmanifest"))).Descendants(Vsix + "Asset").Where(a => (string?)a.Attribute("Type") == type).ToArray();

    [Fact, Trait("Category", "Editor")]
    public void New_Project_lists_the_templates_of_the_dotnet_new_package_of_this_release()
    {
        var package = _vsix.Entries.Where(e => e.FullName.StartsWith("Templates/", StringComparison.Ordinal) && e.Name.EndsWith(".nupkg", StringComparison.Ordinal)).ShouldHaveSingleItem();

        using var nested = new ZipArchive(package.Open());
        var nuspec = XDocument.Parse(Read(nested.GetEntry("Daml.Templates.nuspec")!)).Root!;
        var version = nuspec.Descendants().Single(e => e.Name.LocalName == "version").Value;
        var release = (string)XDocument.Parse(Read(Entry("extension.vsixmanifest"))).Descendants(Vsix + "Identity").Single().Attribute("Version")!;
        version.ShouldStartWith(string.Join(".", release.Split('.').Take(3)));
        package.Name.ShouldBe($"Daml.Templates.{version}.nupkg");
        nested.Entries.Select(e => e.FullName).ShouldContain("content/daml-contracts/.template.config/template.json");
        nested.Entries.Select(e => e.FullName).ShouldContain("content/daml-dotnet-app/.template.config/template.json");
    }

    // New Project shows the language tag and then every classification, so a language among the
    // classifications shows up twice.
    [Theory, Trait("Category", "Editor")]
    [InlineData("daml-contracts")]
    [InlineData("daml-dotnet-app")]
    public void New_Project_shows_the_language_of_a_template_once(string template)
    {
        var package = _vsix.Entries.Single(e => e.FullName.StartsWith("Templates/", StringComparison.Ordinal) && e.Name.EndsWith(".nupkg", StringComparison.Ordinal));
        using var nested = new ZipArchive(package.Open());
        var config = JObject.Parse(Read(nested.GetEntry($"content/{template}/.template.config/template.json")!));

        ((string)config["tags"]!["language"]!).ShouldBe("Daml");
        config["classifications"]!.Values<string>().ShouldNotContain("Daml");
        config["classifications"]!.Values<string>().ShouldContain("Windows");
    }

    // Visual Studio makes the solution itself and adds the template's primary outputs to it, so there the
    // app template leaves out its .slnx and names its three projects instead.
    [Fact, Trait("Category", "Editor")]
    public void In_Visual_Studio_the_app_template_adds_its_projects_to_the_solution_Visual_Studio_makes()
    {
        var package = _vsix.Entries.Single(e => e.FullName.StartsWith("Templates/", StringComparison.Ordinal) && e.Name.EndsWith(".nupkg", StringComparison.Ordinal));
        using var nested = new ZipArchive(package.Open());
        var config = JObject.Parse(Read(nested.GetEntry("content/daml-dotnet-app/.template.config/template.json")!));
        const string Ide = "(HostIdentifier != \"dotnetcli\" && HostIdentifier != \"dotnetcli-preview\")";

        ((string)config["symbols"]!["HostIdentifier"]!["binding"]!).ShouldBe("HostIdentifier");
        config["sources"]!.SelectMany(s => s["modifiers"]!).Where(m => (string?)m["condition"] == Ide).SelectMany(m => m["exclude"]!.Values<string>())
            .ShouldBe(["DamlApp.slnx"]);
        config["primaryOutputs"]!.Where(o => (string?)o["condition"] == Ide).Select(o => (string)o["path"]!).ShouldBe(
            ["DamlApp/DamlApp.csproj", "DamlApp.Contracts/DamlApp.Contracts.msbuildproj", "DamlApp.Contracts.Tests/DamlApp.Contracts.Tests.msbuildproj"]);
    }

    [Fact, Trait("Category", "Editor")]
    public void A_pkgdef_points_the_template_engine_at_the_package_folder()
    {
        var lines = Read(Entry(@"Templates/DamlTemplates.pkgdef")).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith(";", StringComparison.Ordinal));

        // $PackageFolder$ is the pkgdef's own folder, which already is the one with the package.
        lines.ShouldBe([@"[$RootKey$\TemplateEngine\Templates\DamlTemplates]", @"""InstalledPath""=""$PackageFolder$"""]);
        Assets("Microsoft.VisualStudio.VsPackage").Select(a => (string)a.Attribute("Path")!).ShouldContain(@"Templates\DamlTemplates.pkgdef");
    }

    // Visual Studio lists .msbuildproj projects as "CPS Projects", the project type the templates name.
    [Fact, Trait("Category", "Editor")]
    public void Add_New_Item_offers_the_Daml_item_templates_in_Daml_projects()
    {
        var asset = Assets("Microsoft.VisualStudio.ItemTemplate").ShouldHaveSingleItem();
        var containers = XDocument.Parse(Read(Entry("ItemTemplates/templateManifest0.1033.vstman"))).Descendants(Manifest + "VSTemplateContainer").ToList();

        ((string)asset.Attribute("Path")!).ShouldBe("ItemTemplates");
        containers.Select(c => c.Descendants(Template + "Name").Single().Value).ShouldBe(["Daml Module", "Daml Script Test"], ignoreOrder: true);
        containers.ShouldAllBe(c => c.Descendants(Template + "ProjectType").Single().Value == "CPS Projects");
    }

    // Each template makes one .daml file named as the user typed, with the module named after it.
    [Theory, Trait("Category", "Editor")]
    [InlineData("DamlModule")]
    [InlineData("DamlScriptTest")]
    public void Each_item_template_adds_a_Daml_module_named_after_its_file(string name)
    {
        var folder = $"ItemTemplates/CPS Projects/Daml/1033/{name}/";
        var item = XDocument.Parse(Read(Entry(folder + name + ".vstemplate"))).Descendants(Template + "ProjectItem").ShouldHaveSingleItem();

        ((string?)item.Attribute("ReplaceParameters")).ShouldBe("true");
        ((string?)item.Attribute("TargetFileName")).ShouldBe("$fileinputname$.daml");
        Read(Entry(folder + item.Value)).Split('\n').Select(l => l.TrimEnd('\r')).ShouldContain("module $safeitemname$ where");
    }
}
