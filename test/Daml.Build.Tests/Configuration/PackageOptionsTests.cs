using System.Collections.Generic;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Configuration;

public class PackageOptionsTests
{
    private static (PackageOptions Options, List<ConfigurationError> Errors) Read(string? buildEnabled)
    {
        var errors = new List<ConfigurationError>();
        return (PackageOptions.Read(name => name == "DamlBuildEnabled" ? buildEnabled : null, errors), errors);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("Yes", true)]
    [InlineData("false", false)]
    [InlineData("OFF", false)]
    [InlineData("no", false)]
    public void The_build_is_on_unless_switched_off_the_MSBuild_way(string? value, bool enabled)
    {
        var (options, errors) = Read(value);

        options.BuildEnabled.ShouldBe(enabled);
        errors.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Anything_else_is_DAML0100_and_the_build_stays_on()
    {
        var (options, errors) = Read("sometimes");

        options.BuildEnabled.ShouldBeTrue();
        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        error.Message.ShouldContain("DamlBuildEnabled is 'sometimes'");
    }
}
