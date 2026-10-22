using System.Linq;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Tasks;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tasks;

public class ReadDamlManifestTests
{
    private const string Root = @"C:\work\Contracts";
    private const string Manifest = Root + @"\daml.yaml";

    private readonly InMemoryFileSystem _files = new InMemoryFileSystem().AddDirectory(Root + @"\daml");
    private readonly FakeBuildEngine _engine = new();

    private (bool Succeeded, ReadDamlManifest Task) Run(string yaml)
    {
        _files.AddFile(Manifest, yaml);
        var task = new ReadDamlManifest(_files) { BuildEngine = _engine, ManifestPath = Manifest };
        return (task.Execute(), task);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_package_comes_out_as_properties_and_items()
    {
        _files.AddFile(@"C:\work\multi-package.yaml", "sdk-version: 3.6.0\npackages: [Contracts]\n");
        var (succeeded, task) = Run(
            "sdk-version: 3.6.0\nname: contracts\nversion: 1.2.0\nsource: daml\n" +
            "components:\n  - oci://ghcr.io/example/tool:1.0\n" +
            "dependencies:\n  - daml-stdlib\n  - ../lib/finance-1.0.0.dar\n" +
            "data-dependencies:\n  - ../vendor/ledger.dar\n");

        succeeded.ShouldBeTrue();
        _engine.Errors.ShouldBeEmpty();
        (task.PackageName, task.PackageVersion, task.SdkVersion).ShouldBe(("contracts", "1.2.0", "3.6.0"));
        task.PackageRoot.ShouldBe(Root);
        task.MultiPackageManifestPath.ShouldBe(@"C:\work\multi-package.yaml");
        (task.SourcePath, task.SourceKind).ShouldBe((Root + @"\daml", "Folder"));
        task.DarPath.ShouldBe(Root + @"\.daml\dist\contracts-1.2.0.dar");
        task.Components.Select(c => c.ItemSpec).ShouldBe(["oci://ghcr.io/example/tool:1.0"]);
        task.Dependencies.Select(d => (d.ItemSpec, d.GetMetadata("DarPath"))).ShouldBe(
            [("daml-stdlib", ""), ("../lib/finance-1.0.0.dar", @"C:\work\lib\finance-1.0.0.dar")]);
        task.DataDependencies.Select(d => (d.ItemSpec, d.GetMetadata("Written"))).ShouldBe(
            [(@"C:\work\vendor\ledger.dar", "../vendor/ledger.dar")]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Each_manifest_problem_is_an_error_at_its_place_in_daml_yaml()
    {
        var (succeeded, task) = Run("version: 1.2.0\nsource: daml\ndependencies:\n  - daml-stdlib\n  - [nested]\n");

        succeeded.ShouldBeFalse();
        _engine.Errors.Select(e => (e.Code, e.File, e.LineNumber, e.ColumnNumber, e.Message)).ShouldBe(
        [
            (DamlDiagnosticCodes.InvalidConfiguration, Manifest, 5, 5, "Every entry of 'dependencies' must be a single value; this one is skipped."),
            (DamlDiagnosticCodes.InvalidConfiguration, Manifest, 0, 0, "daml.yaml has no 'name'; dpm needs one to build the package."),
        ]);
        task.PackageName.ShouldBeNull();
        task.DarPath.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_missing_manifest_is_an_error_naming_it()
    {
        var task = new ReadDamlManifest(_files) { BuildEngine = _engine, ManifestPath = Manifest };

        task.Execute().ShouldBeFalse();
        var error = _engine.Errors.ShouldHaveSingleItem();
        (error.Code, error.File).ShouldBe((DamlDiagnosticCodes.InvalidConfiguration, Manifest));
    }

    // Messages carry text from the manifest; braces in it must not be taken for format items.
    [Fact, Trait("Category", "Unit")]
    public void Braces_in_a_message_stay_as_written()
    {
        Run("name: contracts\nversion: 1.2.0\nsource: '{src}'\n").Succeeded.ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Message.ShouldStartWith("The source '{src}' in daml.yaml doesn't exist");
    }
}
