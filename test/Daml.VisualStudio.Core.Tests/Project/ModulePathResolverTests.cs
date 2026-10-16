using System.Collections.Generic;
using System.Linq;
using Daml.Build.Core.Manifests;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Project;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Project;

public class ModulePathResolverTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly YamlManifestReader _reader;
    private readonly ModulePathResolver _resolver;

    public ModulePathResolverTests()
    {
        _reader = new YamlManifestReader(_files);
        _resolver = new ModulePathResolver(_files);
    }

    private DamlPackageManifest Package(string root, string yaml)
    {
        _files.AddFile(root + @"\daml.yaml", yaml);
        return _reader.ReadPackage(root + @"\daml.yaml").Value.ShouldNotBeNull();
    }

    private void Module(string path, string name) => _files.AddFile(path, $"module {name} where\n");

    private ModuleResolution Resolve(string module, DamlPackageManifest importing, params DamlPackageManifest[] workspace) =>
        _resolver.Resolve(module, importing, new List<DamlPackageManifest>(workspace) { importing });

    [Fact, Trait("Category", "Unit")]
    public void A_module_of_the_package_itself_maps_to_its_path_under_source()
    {
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\nsource: daml\n");
        Module(@"C:\repo\app\daml\Finance\Asset.daml", "Finance.Asset");

        var result = Resolve("Finance.Asset", app);

        result.Status.ShouldBe(ModuleResolutionStatus.Found);
        result.Path.ShouldBe(@"C:\repo\app\daml\Finance\Asset.daml");
        result.PackageRoot.ShouldBe(@"C:\repo\app");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_single_file_source_roots_modules_at_its_folder()
    {
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\nsource: daml/Main.daml\n");
        Module(@"C:\repo\app\daml\Main.daml", "Main");
        Module(@"C:\repo\app\daml\Lib\Util.daml", "Lib.Util");

        Resolve("Main", app).Path.ShouldBe(@"C:\repo\app\daml\Main.daml");
        Resolve("Lib.Util", app).Path.ShouldBe(@"C:\repo\app\daml\Lib\Util.daml");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_prefixed_module_resolves_in_the_package_the_prefix_names()
    {
        var lib = Package(@"C:\repo\lib", "name: lib\nversion: 1.0.0\nsource: daml\n");
        Module(@"C:\repo\lib\daml\Asset.daml", "Asset");
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\nsource: daml\nmodule-prefixes:\n  lib-1.0.0: V1\n");

        var result = Resolve("V1.Asset", app, lib);

        result.Status.ShouldBe(ModuleResolutionStatus.Found);
        result.Path.ShouldBe(@"C:\repo\lib\daml\Asset.daml");
        result.ModuleName.ShouldBe("Asset");
        result.PackageRoot.ShouldBe(@"C:\repo\lib");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_prefix_for_a_package_outside_the_workspace_is_missing_with_the_reason()
    {
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\nsource: daml\nmodule-prefixes:\n  lib-2.0.0: V2\n");

        var result = Resolve("V2.Asset", app);

        result.Status.ShouldBe(ModuleResolutionStatus.Missing);
        result.Path.ShouldBeNull();
        result.Reason.ShouldNotBeNull().ShouldContain("lib-2.0.0");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_data_dependency_built_from_a_workspace_package_makes_its_modules_visible()
    {
        var interfaces = Package(@"C:\repo\interfaces", "name: interfaces\nversion: 1.0.0\nsource: daml\n");
        Module(@"C:\repo\interfaces\daml\Asset.daml", "Asset");
        var main = Package(@"C:\repo\main", "name: main\nversion: 1.0.0\nsource: daml\ndata-dependencies:\n  - ../interfaces/.daml/dist/interfaces-1.0.0.dar\n");

        var result = Resolve("Asset", main, interfaces);

        result.Status.ShouldBe(ModuleResolutionStatus.Found);
        result.Path.ShouldBe(@"C:\repo\interfaces\daml\Asset.daml");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_package_that_is_not_a_dependency_is_never_searched()
    {
        // Same module name in an unrelated package: no navigation to the first lookalike.
        var other = Package(@"C:\repo\other", "name: other\nversion: 1.0.0\nsource: daml\n");
        Module(@"C:\repo\other\daml\Asset.daml", "Asset");
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\nsource: daml\n");

        var result = Resolve("Asset", app, other);

        result.Status.ShouldBe(ModuleResolutionStatus.Missing);
        result.Candidates.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_dependency_of_another_version_does_not_count()
    {
        var lib = Package(@"C:\repo\lib", "name: lib\nversion: 2.0.0\nsource: daml\n");
        Module(@"C:\repo\lib\daml\Asset.daml", "Asset");
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\nsource: daml\ndata-dependencies:\n  - dars/lib-1.0.0.dar\n");

        Resolve("Asset", app, lib).Status.ShouldBe(ModuleResolutionStatus.Missing);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_same_module_in_the_package_and_a_dependency_is_ambiguous()
    {
        var lib = Package(@"C:\repo\lib", "name: lib\nversion: 1.0.0\nsource: daml\n");
        Module(@"C:\repo\lib\daml\Asset.daml", "Asset");
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\nsource: daml\ndata-dependencies:\n  - ../lib/.daml/dist/lib-1.0.0.dar\n");
        Module(@"C:\repo\app\daml\Asset.daml", "Asset");

        var result = Resolve("Asset", app, lib);

        result.Status.ShouldBe(ModuleResolutionStatus.Ambiguous);
        result.Path.ShouldBeNull();
        result.Candidates.Select(c => c.Path).ShouldBe(new[] { @"C:\repo\app\daml\Asset.daml", @"C:\repo\lib\daml\Asset.daml" }, ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_file_at_the_expected_path_declaring_another_module_is_stale()
    {
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\nsource: daml\n");
        _files.AddFile(@"C:\repo\app\daml\Asset.daml", "module Holding where\n");

        var result = Resolve("Asset", app);

        result.Status.ShouldBe(ModuleResolutionStatus.Stale);
        result.Path.ShouldBeNull();
        var candidate = result.Candidates.ShouldHaveSingleItem();
        candidate.Path.ShouldBe(@"C:\repo\app\daml\Asset.daml");
        candidate.DeclaredModule.ShouldBe("Holding");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_package_without_source_has_no_modules_to_offer()
    {
        var app = Package(@"C:\repo\app", "name: app\nversion: 1.0.0\n");
        Module(@"C:\repo\app\daml\Asset.daml", "Asset");

        var result = Resolve("Asset", app);

        result.Status.ShouldBe(ModuleResolutionStatus.Missing);
        result.Reason.ShouldNotBeNull().ShouldContain("source");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("module Finance.Asset where\n", "Finance.Asset")]
    [InlineData("{-# LANGUAGE ApplicativeDo #-}\n-- a comment\nmodule Main where\n", "Main")]
    [InlineData("{- block\n   module Fake where\n-}\nmodule Real where\n", "Real")]
    [InlineData("{- outer {- inner -} still comment\n module Fake where -}\nmodule Nested where\n", "Nested")]
    [InlineData("{- never closed\nmodule Hidden where\n", null)]
    [InlineData("module Lib.Util\n  ( helper\n  ) where\n", "Lib.Util")]
    [InlineData("\uFEFFmodule Bom where\n", "Bom")]
    [InlineData("template T with p : Party where signatory p\n", null)]
    [InlineData("", null)]
    public void The_declared_module_is_read_from_the_header(string text, string? expected) =>
        ModuleHeader.DeclaredModule(text).ShouldBe(expected);
}
