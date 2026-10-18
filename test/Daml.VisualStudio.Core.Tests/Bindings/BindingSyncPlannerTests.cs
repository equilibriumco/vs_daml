using Daml.Build.Core.Manifests;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Bindings;

// Which C# projects a Daml edit concerns: those generating from the edited package, and from any
// package that takes the edited one's DAR as a dependency, however many steps away.
public class BindingSyncPlannerTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly BindingSyncPlanner _planner;

    private static readonly BindingConsumer App = new(@"C:\sln\App\App.csproj", null, @"C:\sln\Contracts\daml.yaml");
    private static readonly BindingConsumer Web = new(@"C:\sln\Web\Web.csproj", "net10.0", @"C:\sln\Contracts\daml.yaml");
    private static readonly BindingConsumer Tool = new(@"C:\sln\Tool\Tool.csproj", null, @"C:\sln\Other\daml.yaml");

    public BindingSyncPlannerTests()
    {
        _planner = new BindingSyncPlanner(_files, new YamlManifestReader(_files));
        Package(@"C:\sln\Lib", "lib");
        Package(@"C:\sln\Contracts", "contracts", "data-dependencies:\n  - ../Lib/.daml/dist/lib-0.1.0.dar\n");
        Package(@"C:\sln\Other", "other");
    }

    private void Package(string root, string name, string more = "") =>
        _files.AddFile(root + @"\daml.yaml", $"sdk-version: 3.6.0\nname: {name}\nsource: daml\nversion: 0.1.0\n{more}")
              .AddFile(root + @"\daml\Main.daml", "module Main where\n");

    private BindingRefreshTarget[] TargetsFor(string changed) =>
        [.. _planner.TargetsFor(changed, [App, Web, Tool], [@"C:\sln\Lib\daml.yaml", @"C:\sln\Contracts\daml.yaml", @"C:\sln\Other\daml.yaml"])];

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\sln\Contracts\daml\Asset.daml")]
    [InlineData(@"C:\sln\Contracts\daml.yaml")]
    public void An_edit_of_a_Daml_project_concerns_the_CSharp_projects_generating_from_it(string changed)
    {
        TargetsFor(changed).ShouldBe([new BindingRefreshTarget(App.Project, null), new BindingRefreshTarget(Web.Project, "net10.0")]);
    }

    // Lib's DAR goes into Contracts' DAR, which App and Web generate from.
    [Fact, Trait("Category", "Unit")]
    public void An_edit_of_a_dependency_concerns_the_projects_of_the_packages_built_on_it()
    {
        TargetsFor(@"C:\sln\Lib\daml\Lib.daml").ShouldBe([new BindingRefreshTarget(App.Project, null), new BindingRefreshTarget(Web.Project, "net10.0")]);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_unrelated_package_and_a_file_outside_any_package_concern_nobody_else()
    {
        TargetsFor(@"C:\sln\Other\daml\Main.daml").ShouldBe([new BindingRefreshTarget(Tool.Project, null)]);
        TargetsFor(@"C:\scratch\Lone.daml").ShouldBeEmpty();
    }

    // Two generations of one project from the same package (Debug and Release, say) are one refresh.
    [Fact, Trait("Category", "Unit")]
    public void A_project_is_refreshed_once_per_target_framework()
    {
        _planner.TargetsFor(@"C:\sln\Contracts\daml\Asset.daml", [App, App, Web], [@"C:\sln\Contracts\daml.yaml"])
            .ShouldBe([new BindingRefreshTarget(App.Project, null), new BindingRefreshTarget(Web.Project, "net10.0")]);
    }
}
