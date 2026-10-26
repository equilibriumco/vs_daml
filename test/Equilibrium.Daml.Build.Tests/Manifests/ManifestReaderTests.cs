using System.Linq;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Manifests;

public class ManifestReaderTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly YamlManifestReader _reader;

    public ManifestReaderTests() => _reader = new YamlManifestReader(_files);

    [Fact, Trait("Category", "Unit")]
    public void Package_YAML_expands_before_parsing_but_multi_package_YAML_does_not()
    {
        const string PackagePath = @"C:\work\daml.yaml";
        const string MultiPath = @"C:\work\multi-package.yaml";
        _files.AddFile(PackagePath, "source: $SOURCE\ncomponents:\n  - {name: damlc, path: '${COMPILER}'}\ndata-dependencies:\n  - {value: 'oci://registry/pkg:1', main-package-id: abc}\n");
        _files.AddFile(MultiPath, "sdk-version: ${SDK}\n");
        var reader = new YamlManifestReader(_files, name => name switch { "SOURCE" => "daml", "COMPILER" => "../compiler", _ => null });

        var result = reader.ReadPackage(PackagePath);
        result.Diagnostics.ShouldBeEmpty();
        result.Value!.Source.ShouldBe("daml");
        result.Value.LocalComponents["damlc"].ShouldBe("../compiler");
        result.Value.DataDependencies.ShouldBe(["oci://registry/pkg:1"]);
        reader.ReadMultiPackage(MultiPath).Value!.SdkVersion.ShouldBe("${SDK}");
    }

    [Fact, Trait("Category", "Unit")]
    public void Reads_every_field_of_a_package_manifest()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", """
            # comment lines are fine
            sdk-version: 3.6.0
            name: app
            source: daml/Main.daml
            version: 1.2.0
            components:
              - damlc
              - oci://ghcr.io/example/tool:1.0@sha256:abc
            dependencies:
              - daml-prim
              - daml-stdlib
            data-dependencies:
              - dars/lib-1.0.0.dar
            build-options:
              - --target=2.3
            module-prefixes:
              lib-1.0.0: V1
              lib-2.0.0: V2
            """);

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        result.Diagnostics.ShouldBeEmpty();
        var m = result.Value.ShouldNotBeNull();
        m.ManifestPath.ShouldBe(@"C:\work\app\daml.yaml");
        m.PackageRoot.ShouldBe(@"C:\work\app");
        m.SdkVersion.ShouldBe("3.6.0");
        m.Name.ShouldBe("app");
        m.Version.ShouldBe("1.2.0");
        m.Source.ShouldBe("daml/Main.daml");
        m.Components.ShouldBe(new[] { "damlc", "oci://ghcr.io/example/tool:1.0@sha256:abc" });
        m.Dependencies.ShouldBe(new[] { "daml-prim", "daml-stdlib" });
        m.DataDependencies.ShouldBe(new[] { "dars/lib-1.0.0.dar" });
        m.BuildOptions.ShouldBe(new[] { "--target=2.3" });
        m.ModulePrefixes.ShouldBe(new[] { ("lib-1.0.0", "V1"), ("lib-2.0.0", "V2") }.ToDictionary(p => p.Item1, p => p.Item2));
    }

    [Fact, Trait("Category", "Unit")]
    public void Missing_and_empty_fields_come_back_empty_and_unknown_keys_are_ignored()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", """
            name: app
            init-script: Main:setup
            dependencies:
            some-future-key:
              nested: [1, 2]
            """);

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        result.Diagnostics.ShouldBeEmpty();
        var m = result.Value.ShouldNotBeNull();
        m.Name.ShouldBe("app");
        m.SdkVersion.ShouldBeNull();
        m.Source.ShouldBeNull();
        m.Dependencies.ShouldBeEmpty();
        m.Components.ShouldBeEmpty();
        m.ModulePrefixes.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Invalid_yaml_is_reported_with_line_and_column_and_no_manifest()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "name: app\nsource: \"daml\n");

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        result.Value.ShouldBeNull();
        var error = result.Diagnostics.ShouldHaveSingleItem();
        error.Severity.ShouldBe(ManifestSeverity.Error);
        error.Path.ShouldBe(@"C:\work\app\daml.yaml");
        error.Line.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(2);
        error.Column.ShouldNotBeNull().ShouldBeGreaterThan(0);
        error.Message.ShouldNotContain("(Line:");
    }

    [Fact, Trait("Category", "Unit")]
    public void Input_the_yaml_scanner_trips_over_is_an_error_not_a_crash()
    {
        // YamlDotNet throws InvalidOperationException (not YamlException) for an unclosed flow sequence.
        _files.AddFile(@"C:\work\app\daml.yaml", "name: app\nsdk-version: [3.6.0\nsource: daml\n");

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        result.Value.ShouldBeNull();
        result.Diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(ManifestSeverity.Error);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_field_of_the_wrong_shape_is_an_error_at_its_position_and_the_rest_still_reads()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "name: app\ndependencies: daml-prim\nversion: 1.0.0\n");

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        var error = result.Diagnostics.ShouldHaveSingleItem();
        error.Severity.ShouldBe(ManifestSeverity.Error);
        error.Line.ShouldBe(2);
        error.Column.ShouldBe(15);
        error.Message.ShouldContain("dependencies");
        var m = result.Value.ShouldNotBeNull();
        m.Dependencies.ShouldBeEmpty();
        m.Version.ShouldBe("1.0.0");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_list_entry_that_is_not_text_is_an_error_and_is_skipped()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "dependencies:\n  - daml-prim\n  - { name: x }\n  - daml-stdlib\n");

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        result.Diagnostics.ShouldHaveSingleItem().Line.ShouldBe(3);
        result.Value.ShouldNotBeNull().Dependencies.ShouldBe(new[] { "daml-prim", "daml-stdlib" });
    }

    [Fact, Trait("Category", "Unit")]
    public void A_single_value_field_given_as_a_list_is_an_error()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "name: [a, b]\nversion: 1.0.0\n");

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        var error = result.Diagnostics.ShouldHaveSingleItem();
        (error.Line, error.Column).ShouldBe((1, 7));
        error.Message.ShouldContain("'name'");
        result.Value.ShouldNotBeNull().Name.ShouldBeNull();
        result.Value.Version.ShouldBe("1.0.0");
    }

    [Fact, Trait("Category", "Unit")]
    public void Module_prefixes_of_the_wrong_shape_are_errors()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "module-prefixes:\n  lib-1.0.0: V1\n  lib-2.0.0: [V2]\n");
        _files.AddFile(@"C:\work\other\daml.yaml", "module-prefixes:\n  - lib-1.0.0\n");

        var entries = _reader.ReadPackage(@"C:\work\app\daml.yaml");
        var list = _reader.ReadPackage(@"C:\work\other\daml.yaml");

        entries.Diagnostics.ShouldHaveSingleItem().Line.ShouldBe(3);
        entries.Value.ShouldNotBeNull().ModulePrefixes.Keys.ShouldBe(new[] { "lib-1.0.0" });
        list.Diagnostics.ShouldHaveSingleItem().Line.ShouldBe(2);
        list.Value.ShouldNotBeNull().ModulePrefixes.ShouldBeEmpty();
    }

    // daml-finance writes "data-dependencies: null"; damlc reads every plain YAML null as "not given".
    [Theory, Trait("Category", "Unit")]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("NULL")]
    [InlineData("~")]
    public void A_plain_yaml_null_means_the_field_is_not_given(string nothing)
    {
        _files.AddFile(@"C:\work\app\daml.yaml", $"name: app\nsource: {nothing}\ndata-dependencies: {nothing}\nbuild-options: {nothing}\nmodule-prefixes: {nothing}\n");
        _files.AddFile(@"C:\work\multi-package.yaml", $"packages: {nothing}\n");

        var package = _reader.ReadPackage(@"C:\work\app\daml.yaml");
        var multi = _reader.ReadMultiPackage(@"C:\work\multi-package.yaml");

        package.Diagnostics.ShouldBeEmpty();
        var m = package.Value.ShouldNotBeNull();
        m.Name.ShouldBe("app");
        m.Source.ShouldBeNull();
        m.DataDependencies.ShouldBeEmpty();
        m.BuildOptions.ShouldBeEmpty();
        m.ModulePrefixes.ShouldBeEmpty();
        multi.Diagnostics.ShouldBeEmpty();
        multi.Value.ShouldNotBeNull().Packages.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_quoted_null_is_text()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "name: 'null'\ndependencies: \"~\"\n");

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        result.Value.ShouldNotBeNull().Name.ShouldBe("null");
        result.Diagnostics.ShouldHaveSingleItem().Line.ShouldBe(2);
    }

    [Fact, Trait("Category", "Unit")]
    public void Quoted_empty_values_are_kept_as_empty_text()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "name: \"\"\n");

        _reader.ReadPackage(@"C:\work\app\daml.yaml").Value.ShouldNotBeNull().Name.ShouldBe("");
    }

    [Fact, Trait("Category", "Unit")]
    public void Duplicate_keys_are_reported()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "name: a\nname: b\n");

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        result.Diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(ManifestSeverity.Error);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("# only a comment\n")]
    [InlineData("- just\n- a list\n")]
    [InlineData("just text\n")]
    public void A_manifest_that_is_not_a_mapping_is_an_error(string text)
    {
        _files.AddFile(@"C:\work\app\daml.yaml", text);

        var result = _reader.ReadPackage(@"C:\work\app\daml.yaml");

        result.Value.ShouldBeNull();
        result.Diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(ManifestSeverity.Error);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_missing_file_is_an_error_without_a_position()
    {
        var result = _reader.ReadPackage(@"C:\work\nowhere\daml.yaml");

        result.Value.ShouldBeNull();
        var error = result.Diagnostics.ShouldHaveSingleItem();
        error.Line.ShouldBeNull();
        error.Path.ShouldBe(@"C:\work\nowhere\daml.yaml");
    }

    [Fact, Trait("Category", "Unit")]
    public void Multi_package_paths_resolve_against_the_manifest_folder()
    {
        _files.AddFile(@"C:\work\repo\multi-package.yaml", """
            sdk-version: 3.6.0

            packages:
            - ./daml
            - ./daml/test
            - ../shared/lib
            dars:
            - ./external/x-1.0.0.dar
            """);

        var result = _reader.ReadMultiPackage(@"C:\work\repo\multi-package.yaml");

        result.Diagnostics.ShouldBeEmpty();
        var m = result.Value.ShouldNotBeNull();
        m.ManifestPath.ShouldBe(@"C:\work\repo\multi-package.yaml");
        m.SdkVersion.ShouldBe("3.6.0");
        m.Packages.ShouldBe(new[] { @"C:\work\repo\daml", @"C:\work\repo\daml\test", @"C:\work\shared\lib" });
        m.Dars.ShouldBe(new[] { @"C:\work\repo\external\x-1.0.0.dar" });
    }

    [Fact, Trait("Category", "Unit")]
    public void Multi_package_errors_carry_positions_too()
    {
        _files.AddFile(@"C:\work\repo\multi-package.yaml", "packages: ./daml\n");

        var result = _reader.ReadMultiPackage(@"C:\work\repo\multi-package.yaml");

        result.Diagnostics.ShouldHaveSingleItem().Line.ShouldBe(1);
        result.Value.ShouldNotBeNull().Packages.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Diagnostics_print_in_the_canonical_compiler_format()
    {
        new ManifestDiagnostic(@"C:\a\daml.yaml", 3, 7, ManifestSeverity.Error, "bad").ToString().ShouldBe(@"C:\a\daml.yaml(3,7): error: bad");
        new ManifestDiagnostic(@"C:\a\daml.yaml", null, null, ManifestSeverity.Warning, "odd").ToString().ShouldBe(@"C:\a\daml.yaml: warning: odd");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_package_entry_that_isnt_a_path_is_reported_and_the_rest_are_kept()
    {
        // A NUL is refused by every file system, which keeps the test the same on every runtime.
        _files.AddFile(@"C:\work\repo\multi-package.yaml", "packages:\n  - app\n  - \"lib\\0core\"\n  - tests\n");

        var result = _reader.ReadMultiPackage(@"C:\work\repo\multi-package.yaml");

        result.Value.ShouldNotBeNull().Packages.ShouldBe([@"C:\work\repo\app", @"C:\work\repo\tests"]);
        var problem = result.Diagnostics.ShouldHaveSingleItem();
        problem.Severity.ShouldBe(ManifestSeverity.Error);
        problem.Line.ShouldBe(3);
        problem.Message.ShouldContain("lib");
    }

    [Fact, Trait("Category", "Unit")]
    public void Exists_says_whether_the_manifest_file_is_there()
    {
        _files.AddFile(@"C:\work\app\daml.yaml", "name: app\n");

        _reader.Exists(@"C:\work\app\daml.yaml").ShouldBeTrue();
        _reader.Exists(@"C:\work\other\daml.yaml").ShouldBeFalse();
    }

    // YAML has more than the hand-written manifests use; none of it may throw out of the reader.
    [Theory, Trait("Category", "Unit")]
    [InlineData("sdk-version: 3.6\nname: app\n", "3.6")]
    [InlineData("\uFEFFsdk-version: 3.6.0\r\nname: app\r\n", "3.6.0")]
    [InlineData("sdk-version: '3.6.0'\nname: app\n", "3.6.0")]
    [InlineData("base: &b\n  name: app\nsdk-version: 3.6.0\nmore: *b\n", "3.6.0")]
    [InlineData("base: &b\n  name: app\n<<: *b\nsdk-version: 3.6.0\n", "3.6.0")]
    [InlineData("sdk-version: &v 3.6.0\nname: *v\n", "3.6.0")]
    [InlineData("sdk-version: [3, 6]\nname: app\n", null)]
    public void Unusual_but_valid_yaml_is_read_or_reported_never_thrown(string yaml, string? sdkVersion)
    {
        _files.AddFile(@"C:\work\app\daml.yaml", yaml);

        var result = Should.NotThrow(() => _reader.ReadPackage(@"C:\work\app\daml.yaml"));

        result.Value.ShouldNotBeNull().SdkVersion.ShouldBe(sdkVersion);
        if (sdkVersion is null)
        {
            result.Diagnostics.ShouldNotBeEmpty("the sequence where a version was expected is reported");
        }
    }
}
