using System.Collections.Generic;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Configuration;

public class CodegenOptionsTests
{
    private const string Project = @"C:\work\App";
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static (CodegenOptions Options, List<ConfigurationError> Errors) Read(params (string Name, string Value)[] properties)
    {
        var values = new Dictionary<string, string>
        {
            ["MSBuildProjectDirectory"] = Project,
            ["BaseIntermediateOutputPath"] = @"obj\",
            ["IntermediateOutputPath"] = @"obj\Debug\net10.0\",
        };
        foreach (var (name, value) in properties)
        {
            values[name] = value;
        }
        var errors = new List<ConfigurationError>();
        return (CodegenOptions.Read(name => values.TryGetValue(name, out var value) ? value : null, errors), errors);
    }

    [Fact, Trait("Category", "Unit")]
    public void By_default_the_released_generator_writes_under_the_intermediate_folder_of_the_build()
    {
        var (options, errors) = Read();

        errors.ShouldBeEmpty();
        options.Enabled.ShouldBeTrue();
        options.Component!.Reference.ShouldBe(DamlProperties.CodegenComponent.Default);
        options.OutputMode.ShouldBe(GeneratedOutputMode.Intermediate);
        options.GeneratedRoot.ShouldBe(@"C:\work\App\obj\Debug\net10.0\Daml");
    }

    [Fact, Trait("Category", "Unit")]
    public void Source_mode_writes_into_the_project_by_default()
    {
        var (options, errors) = Read(("DamlGeneratedOutputMode", "source"));

        errors.ShouldBeEmpty();
        options.OutputMode.ShouldBe(GeneratedOutputMode.Source);
        options.GeneratedRoot.ShouldBe(@"C:\work\App\Generated\Daml");
    }

    // One folder in the project can't belong to several target frameworks at once.
    [Fact, Trait("Category", "Unit")]
    public void Source_mode_takes_a_single_target_framework()
    {
        var error = Read(("DamlGeneratedOutputMode", "Source"), ("TargetFrameworks", "net10.0; net11.0")).Errors.ShouldHaveSingleItem();

        error.Code.ShouldBe(DamlDiagnosticCodes.OutputConflict);
        error.Message.ShouldContain("net10.0; net11.0");
        error.Message.ShouldContain("Intermediate");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Source", "net10.0")]
    [InlineData("Source", "net10.0;")]
    [InlineData("Intermediate", "net10.0;net11.0")]
    public void One_target_framework_or_intermediate_mode_is_fine(string mode, string targetFrameworks) =>
        Read(("DamlGeneratedOutputMode", mode), ("TargetFrameworks", targetFrameworks)).Errors.ShouldBeEmpty();

    [Theory, Trait("Category", "Unit")]
    [InlineData("Intermediate", @"obj\Debug\net10.0\Bindings", @"C:\work\App\obj\Debug\net10.0\Bindings")]
    [InlineData("Source", @"Bindings\", @"C:\work\App\Bindings")]
    [InlineData("Source", @"..\Shared\Generated", @"C:\work\Shared\Generated")]
    public void A_chosen_folder_resolves_against_the_project(string mode, string folder, string expected)
    {
        var (options, errors) = Read(("DamlGeneratedOutputMode", mode), ("DamlGeneratedDir", folder));

        errors.ShouldBeEmpty();
        options.GeneratedRoot.ShouldBe(expected);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_intermediate_folder_outside_the_project_is_followed()
    {
        var (options, errors) = Read(
            ("BaseIntermediateOutputPath", @"C:\work\artifacts\obj\App\"),
            ("IntermediateOutputPath", @"C:\work\artifacts\obj\App\debug\"));

        errors.ShouldBeEmpty();
        options.GeneratedRoot.ShouldBe(@"C:\work\artifacts\obj\App\debug\Daml");
    }

    // A root holding the project would mix its sources with the generator's files. In
    // intermediate mode each configuration and target framework needs a root of its own, which
    // only a folder inside that build's intermediate folder guarantees.
    [Theory, Trait("Category", "Unit")]
    [InlineData("Source", ".")]
    [InlineData("Source", "..")]
    [InlineData("Source", @"C:\")]
    [InlineData("Intermediate", "Generated")]
    [InlineData("Intermediate", @"obj\custom")]
    [InlineData("Intermediate", @"obj\Debug\net10.0")]
    public void A_folder_that_holds_the_project_or_is_shared_between_builds_in_intermediate_mode_is_refused(string mode, string folder)
    {
        var (options, errors) = Read(("DamlGeneratedOutputMode", mode), ("DamlGeneratedDir", folder));

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        error.Message.ShouldContain("DamlGeneratedDir");
        options.GeneratedRoot.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void An_unknown_output_mode_is_refused()
    {
        var (options, errors) = Read(("DamlGeneratedOutputMode", "Both"));

        errors.ShouldHaveSingleItem().Message.ShouldContain("'Both'");
        options.GeneratedRoot.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Another_pinned_generator_can_be_chosen()
    {
        var reference = $"oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.7.0@sha256:{Digest}";

        var (options, errors) = Read(("DamlCodegenComponent", reference));

        errors.ShouldBeEmpty();
        options.Component!.Digest.ShouldBe(Digest);
    }

    // The digest names the generator's private cache, so an unpinned generator can't be used
    // even with the preflight checks skipped.
    [Fact, Trait("Category", "Unit")]
    public void A_generator_without_a_digest_is_a_pin_error_even_without_preflight()
    {
        var (options, errors) = Read(("DamlCodegenComponent", "oci://ghcr.io/peacefulstudio/dpm-codegen-cs:latest"), ("DamlSkipPreflight", "true"));

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.GeneratorPinConflict);
        error.Message.ShouldContain("DamlCodegenComponent");
        options.Component.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Codegen_can_be_switched_off() =>
        Read(("DamlCodegenEnabled", "false")).Options.Enabled.ShouldBeFalse();

    [Fact, Trait("Category", "Unit")]
    public void A_non_boolean_switch_is_refused() =>
        Read(("DamlCodegenEnabled", "sometimes")).Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
}
