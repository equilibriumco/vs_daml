using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Project;

// Whether a solution's Daml packages need a multi-package.yaml for the server to navigate between
// them, and what the one it offers to create says.
public class MultiPackageAdvisorTests
{
    private const string Solution = @"C:\sln";

    private readonly InMemoryFileSystem _files = new();
    private readonly MultiPackageAdvisor _advisor;

    public MultiPackageAdvisorTests() => _advisor = new MultiPackageAdvisor(_files, new YamlManifestReader(_files));

    // The package's daml.yaml, as a project's EqDamlManifestPath names it.
    private string Package(string root, string name = "pkg")
    {
        var manifest = root + @"\daml.yaml";
        _files.AddFile(manifest, $"sdk-version: 3.6.0\nname: {name}\nsource: daml\nversion: 1.0.0\n");
        return manifest;
    }

    [Fact, Trait("Category", "Unit")]
    public void One_package_needs_nothing()
    {
        _advisor.Advise(Solution, [Package(@"C:\sln\Contracts")]).Kind.ShouldBe(MultiPackageAdvice.None);
    }

    [Fact, Trait("Category", "Unit")]
    public void Two_packages_without_a_multi_package_yaml_get_one_offered_in_the_solution_folder()
    {
        var proposal = _advisor.Advise(Solution, [Package(@"C:\sln\Contracts"), Package(@"C:\sln\Contracts.Tests")]);

        proposal.Kind.ShouldBe(MultiPackageAdvice.Create);
        proposal.ManifestPath.ShouldBe(@"C:\sln\multi-package.yaml");
        proposal.Packages.ShouldBe([@"C:\sln\Contracts", @"C:\sln\Contracts.Tests"]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\sln\multi-package.yaml", "packages:\n- ./Contracts\n- ./Contracts.Tests\n")]
    [InlineData(@"C:\multi-package.yaml", "packages:\n- ./sln/Contracts\n- ./sln/Contracts.Tests\n")]
    public void Packages_one_multi_package_yaml_lists_need_nothing(string path, string content)
    {
        _files.AddFile(path, content);

        _advisor.Advise(Solution, [Package(@"C:\sln\Contracts"), Package(@"C:\sln\Contracts.Tests")]).Kind.ShouldBe(MultiPackageAdvice.None);
    }

    // The user's own file is never rewritten; what it lacks is pointed out instead.
    [Fact, Trait("Category", "Unit")]
    public void A_multi_package_yaml_in_the_solution_folder_that_misses_packages_is_pointed_out()
    {
        _files.AddFile(@"C:\sln\multi-package.yaml", "packages:\n- ./Contracts\n");

        var proposal = _advisor.Advise(Solution, [Package(@"C:\sln\Contracts"), Package(@"C:\sln\Contracts.Tests"), Package(@"C:\shared\Lib")]);

        proposal.Kind.ShouldBe(MultiPackageAdvice.Incomplete);
        proposal.ManifestPath.ShouldBe(@"C:\sln\multi-package.yaml");
        proposal.Packages.ShouldBe([@"C:\sln\Contracts.Tests", @"C:\shared\Lib"]);
    }

    // Each listed by a multi-package.yaml of its own: none of them takes the server to the other.
    [Fact, Trait("Category", "Unit")]
    public void Packages_in_different_multi_package_yamls_get_one_offered_for_all()
    {
        _files.AddFile(@"C:\sln\A\multi-package.yaml", "packages:\n- ./pkg\n");
        _files.AddFile(@"C:\sln\B\multi-package.yaml", "packages:\n- ./pkg\n");

        var proposal = _advisor.Advise(Solution, [Package(@"C:\sln\A\pkg"), Package(@"C:\sln\B\pkg")]);

        proposal.Kind.ShouldBe(MultiPackageAdvice.Create);
        proposal.Packages.ShouldBe([@"C:\sln\A\pkg", @"C:\sln\B\pkg"]);
    }

    // A project without its daml.yaml yet isn't a package; two projects of one package are one.
    [Fact, Trait("Category", "Unit")]
    public void Missing_manifests_and_repeated_packages_are_left_out()
    {
        var contracts = Package(@"C:\sln\Contracts");

        _advisor.Advise(Solution, [contracts, contracts.ToUpperInvariant(), @"C:\sln\New\daml.yaml"]).Kind.ShouldBe(MultiPackageAdvice.None);
    }

    // What the created file says reads back as the same packages, whatever their folder names.
    [Fact, Trait("Category", "Unit")]
    public void The_offered_file_lists_the_packages_relative_to_the_solution_folder()
    {
        var packages = new[] { @"C:\sln\Contracts", @"C:\sln\my app\Contracts #1", @"C:\shared\Lib", @"D:\elsewhere\Pkg" };

        var content = MultiPackageAdvisor.Content(Solution, packages);
        _files.AddFile(@"C:\sln\multi-package.yaml", content);

        content.ShouldContain("  - ./Contracts\n");
        content.ShouldContain("  - ../shared/Lib\n");
        new YamlManifestReader(_files).ReadMultiPackage(@"C:\sln\multi-package.yaml").Value.ShouldNotBeNull().Packages.ShouldBe(packages);
    }
}
