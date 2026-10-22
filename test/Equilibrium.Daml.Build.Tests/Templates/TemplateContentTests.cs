using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Templates;

// What the dotnet new templates hold, read from the repository: the two templates create the same
// Daml packages, and the projects take their package versions from packing.
public class TemplateContentTests
{
    private static readonly string Content = Path.Combine(Repository.Root, "src", "Daml.Templates", "content");

    private static string Contracts(string relativePath) => Path.Combine(Content, "daml-contracts", relativePath);

    private static string App(string relativePath) => Path.Combine(Content, "daml-dotnet-app", relativePath);

    // The app template's names in the contracts template's spelling.
    private static string AsContracts(string text) =>
        text.Replace("DamlApp.Contracts", "DamlContracts").Replace("daml-app-contracts", "daml-contracts");

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"DamlContracts\daml\Main.daml", @"DamlApp.Contracts\daml\Main.daml")]
    [InlineData(@"DamlContracts\daml.yaml", @"DamlApp.Contracts\daml.yaml")]
    [InlineData(@"DamlContracts\DamlContracts.msbuildproj", @"DamlApp.Contracts\DamlApp.Contracts.msbuildproj")]
    [InlineData(@"DamlContracts.Tests\daml\Test.daml", @"DamlApp.Contracts.Tests\daml\Test.daml")]
    [InlineData(@"DamlContracts.Tests\daml.yaml", @"DamlApp.Contracts.Tests\daml.yaml")]
    [InlineData(@"DamlContracts.Tests\DamlContracts.Tests.msbuildproj", @"DamlApp.Contracts.Tests\DamlApp.Contracts.Tests.msbuildproj")]
    [InlineData("multi-package.yaml", "multi-package.yaml")]
    [InlineData(".gitignore", ".gitignore")]
    public void Both_templates_create_the_same_Daml_packages(string contracts, string app) =>
        AsContracts(File.ReadAllText(App(app))).ShouldBe(File.ReadAllText(Contracts(contracts)));

    // Templates must use the exact package pair checked by codegen preflight.
    [Theory, Trait("Category", "Unit")]
    [InlineData("Daml.Runtime")]
    [InlineData("Daml.Ledger.Abstractions")]
    public void The_app_references_the_runtime_version_the_generator_writes_for(string package)
    {
        var reference = XDocument.Load(App(@"DamlApp\DamlApp.csproj")).Descendants("PackageReference")
            .Single(r => (string?)r.Attribute("Include") == package);

        var version = new Version((string)reference.Attribute("Version")!);

        version.ShouldBe(ReleaseTools.Runtime.Version);
    }

    // dotnet sln add doesn't know Daml projects, and neither host builds a solution entry without a type.
    [Fact, Trait("Category", "Unit")]
    public void The_solution_holds_each_project_and_names_the_Daml_ones_by_type()
    {
        var projects = XDocument.Load(App("DamlApp.slnx")).Descendants("Project")
            .ToDictionary(p => (string)p.Attribute("Path")!, p => (string?)p.Attribute("Type"));

        projects.ShouldBe(new()
        {
            ["DamlApp/DamlApp.csproj"] = null,
            ["DamlApp.Contracts/DamlApp.Contracts.msbuildproj"] = "13b669be-bb05-4ddf-9536-439f39a36129",
            ["DamlApp.Contracts.Tests/DamlApp.Contracts.Tests.msbuildproj"] = "13b669be-bb05-4ddf-9536-439f39a36129",
        });
    }

    // Projects use the Daml packages of the release that created them; packing writes it into template.json.
    [Theory, Trait("Category", "Unit")]
    [InlineData("daml-contracts")]
    [InlineData("daml-dotnet-app")]
    public void Projects_take_the_Daml_packages_of_the_release_that_packed_the_template(string template)
    {
        var folder = Path.Combine(Content, template);
        var config = File.ReadAllText(Path.Combine(folder, ".template.config", "template.json"));

        config.ShouldMatch(@"""value"":\s*""@PackageVersion@""\s*},\s*""replaces"":\s*""DAML_PACKAGES_VERSION""");
        config.ShouldMatch(@"""value"":\s*""@PackageIdPrefix@""\s*},\s*""replaces"":\s*""DAML_PACKAGE_PREFIX""");
        foreach (var project in Directory.GetFiles(folder, "*.msbuildproj", SearchOption.AllDirectories))
        {
            XDocument.Load(project).Root!.Attribute("Sdk")!.Value.ShouldBe("DAML_PACKAGE_PREFIX.Sdk/DAML_PACKAGES_VERSION", project);
        }
        foreach (var project in Directory.GetFiles(folder, "*.csproj", SearchOption.AllDirectories))
        {
            var reference = XDocument.Load(project).Descendants("PackageReference").Single(r => ((string)r.Attribute("Include")!).EndsWith(".Build.CSharp", StringComparison.Ordinal));
            ((string)reference.Attribute("Include")!, (string)reference.Attribute("Version")!).ShouldBe(("DAML_PACKAGE_PREFIX.Build.CSharp", "DAML_PACKAGES_VERSION"), project);
        }
    }
}
