using System;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.Tools;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tools;

public class DpmVersionTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("version: 1.0.22\nbuild: b65a815\nbuildDate: b65a815\n", "1.0.22")]
    [InlineData("version: 1.1.0-rc.2\n", "1.1.0")]
    [InlineData("\u001b[0mversion: 2.0.3 \r\n", "2.0.3")]
    public void The_version_is_read_from_dpm_version_output(string output, string expected) =>
        DpmVersion.Parse(output.Split('\n')).ShouldBe(Version.Parse(expected));

    [Theory, Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("   3.5.2 \n * 3.6.0 \n")]
    [InlineData("version: latest\n")]
    [InlineData("version: 999999999999999999999.0.0\n")]
    public void Output_without_a_version_gives_none(string output) =>
        DpmVersion.Parse(output.Split('\n')).ShouldBeNull();

    [Theory, Trait("Category", "Unit")]
    [InlineData("1.0.22")]
    [InlineData("1.1.0")]
    public void The_tested_version_and_later_ones_pass(string version) =>
        DpmVersion.Problem(Version.Parse(version), []).ShouldBeNull();

    [Fact, Trait("Category", "Unit")]
    public void An_older_dpm_is_DAML0103_with_a_way_out()
    {
        var problem = DpmVersion.Problem(new Version(1, 0, 10), []).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.DpmTooOld);
        problem.Message.ShouldBe(
            "dpm 1.0.10 is older than 1.0.22, the oldest this Daml build supports. Install a newer dpm, " +
            "or set EqDamlSkipPreflight to true to build with this one at your own risk.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_version_that_cant_be_read_quotes_what_dpm_printed()
    {
        var problem = DpmVersion.Problem(null, ["", "   3.5.2 ", " * 3.6.0 ", "a", "b", "c", "d"]).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.DpmTooOld);
        problem.Message.ShouldBe(
            "'dpm --version' printed no version this Daml build knows, so it can't tell whether dpm is new enough " +
            "(1.0.22 or later). It printed:\n3.5.2\n* 3.6.0\na\nb\nc");
    }

    [Fact, Trait("Category", "Unit")]
    public void Version_probe_failures_mask_URL_credentials()
    {
        DpmVersion.Problem(null, ["https://user:secret@registry"])!.Message.ShouldNotContain("secret");
        JavaVersion.Problem(null, "java", ["oci://user:secret@registry"])!.Message.ShouldNotContain("secret");
    }
}
