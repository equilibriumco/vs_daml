using System.Linq;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.VisualStudio.LanguageServer.Messages;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Messages;

public class WatchedFilesTests
{
    // Told only of the new name, damlc keeps resolving imports of the old module.
    [Fact, Trait("Category", "Unit")]
    public void Renaming_a_Daml_source_reports_the_old_file_gone_and_the_new_one_created()
    {
        WatchedFiles.ForRename(@"C:\repo\daml\Dep.daml", @"C:\repo\daml\Renamed.daml").ShouldBe(
        [
            new FileEvent("file:///C:/repo/daml/Dep.daml", FileChangeType.Deleted),
            new FileEvent("file:///C:/repo/daml/Renamed.daml", FileChangeType.Created),
        ]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\daml\Dep.daml", @"C:\repo\daml\Dep.daml.bak", "Deleted file:///C:/repo/daml/Dep.daml")]
    [InlineData(@"C:\repo\daml\Dep.txt", @"C:\repo\daml\Dep.daml", "Created file:///C:/repo/daml/Dep.daml")]
    [InlineData(@"C:\repo\daml\Dep.DAML", @"C:\repo\daml\Other.Daml", "Deleted file:///C:/repo/daml/Dep.DAML, Created file:///C:/repo/daml/Other.Daml")]
    [InlineData(@"C:\my repo\daml\A.daml", @"C:\my repo\daml\B.daml", "Deleted file:///C:/my%20repo/daml/A.daml, Created file:///C:/my%20repo/daml/B.daml")]
    public void Only_the_names_that_are_Daml_sources_are_reported(string from, string to, string expected)
    {
        string.Join(", ", WatchedFiles.ForRename(from, to).Select(e => $"{e.Type} {e.Uri}")).ShouldBe(expected);
    }

    // damlc's own copies of dependencies, the C# generator's work folder, and other files.
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\.daml\dependencies\2.3\lib-abc\Lib.daml", @"C:\repo\.daml\dependencies\2.3\lib-abc\Lib2.daml")]
    [InlineData(@"C:\repo\App\obj\Debug\net10.0\Daml\.daml-work\9de80d6cfe50bada.run\daml\A.daml", @"C:\repo\App\obj\Debug\net10.0\Daml\.daml-work\9de80d6cfe50bada.run\daml\B.daml")]
    [InlineData(@"C:\repo\daml.yaml", @"C:\repo\daml.yaml.bak")]
    public void Build_output_and_other_files_report_nothing(string from, string to)
    {
        WatchedFiles.ForRename(from, to).ShouldBeEmpty();
    }

    private static DamlPackageManifest Package(string root, string[]? dependencies = null, string[]? dataDependencies = null) => new()
    {
        ManifestPath = root + @"\daml.yaml",
        PackageRoot = root,
        Dependencies = dependencies ?? ["daml-prim", "daml-stdlib"],
        DataDependencies = dataDependencies ?? [],
    };

    // multi-ide restarts the packages that use a DAR when the DAR disappears, but not when it comes
    // back; a change to their daml.yaml does restart them.
    [Fact, Trait("Category", "Unit")]
    public void A_written_DAR_reports_the_manifests_of_the_packages_that_use_it_as_changed()
    {
        var packages = new[]
        {
            Package(@"C:\repo\interfaces"),
            Package(@"C:\repo\main", dataDependencies: ["../interfaces/.daml/dist/interfaces-1.0.0.dar"]),
            Package(@"C:\repo\test", dataDependencies: ["../interfaces/.daml/dist/interfaces-1.0.0.dar", "../main/.daml/dist/main-1.0.0.dar"]),
        };

        WatchedFiles.ForDarWritten(@"C:\repo\interfaces\.daml\dist\interfaces-1.0.0.dar", packages).ShouldBe(
        [
            new FileEvent("file:///C:/repo/main/daml.yaml", FileChangeType.Changed),
            new FileEvent("file:///C:/repo/test/daml.yaml", FileChangeType.Changed),
        ]);
        WatchedFiles.ForDarWritten(@"C:\repo\main\.daml\dist\main-1.0.0.dar", packages).ShouldBe(
        [
            new FileEvent("file:///C:/repo/test/daml.yaml", FileChangeType.Changed),
        ]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"..\lib\.daml\dist\lib-1.0.0.dar")]
    [InlineData("C:/Repo/Lib/.daml/dist/LIB-1.0.0.dar")]
    [InlineData("./../lib/./.daml/dist/lib-1.0.0.dar")]
    public void The_DAR_is_found_however_the_manifest_writes_its_path(string entry)
    {
        WatchedFiles.ForDarWritten(@"C:\repo\lib\.daml\dist\lib-1.0.0.dar", [Package(@"C:\repo\app", dataDependencies: [entry])])
            .ShouldHaveSingleItem().Uri.ShouldBe("file:///C:/repo/app/daml.yaml");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_DAR_in_dependencies_counts_and_a_package_listing_it_twice_is_reported_once()
    {
        var app = Package(@"C:\repo\app", dependencies: ["daml-prim", @"..\lib\lib.dar"], dataDependencies: [@"..\lib\lib.dar"]);

        WatchedFiles.ForDarWritten(@"C:\repo\lib\lib.dar", [app]).ShouldHaveSingleItem().Uri.ShouldBe("file:///C:/repo/app/daml.yaml");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_DAR_no_package_uses_reports_nothing()
    {
        var packages = new[]
        {
            Package(@"C:\repo\lib"),
            Package(@"C:\repo\app", dependencies: ["daml-prim", "daml-script"], dataDependencies: [@"..\other\other.dar", "oci://example.com/pkg:1.0.0"]),
        };

        WatchedFiles.ForDarWritten(@"C:\repo\lib\.daml\dist\lib-1.0.0.dar", packages).ShouldBeEmpty();
    }
}
