using Daml.Build.Core.Codegen;
using Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public class RuntimeCompatibilityTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("net10.0", "0.6.0")]
    [InlineData("net10.0-windows", "0.6.3")]
    [InlineData("net11.0", "0.6.1-preview.2")]
    public void A_project_on_net10_or_later_with_a_matching_runtime_passes(string targetFramework, string runtime) =>
        RuntimeCompatibility.Problems(targetFramework, runtime).ShouldBeEmpty();

    [Theory, Trait("Category", "Unit")]
    [InlineData("net48")]
    [InlineData("net8.0")]
    [InlineData("netstandard2.0")]
    [InlineData("netcoreapp3.1")]
    public void An_older_target_framework_is_DAML0201(string targetFramework)
    {
        var problem = RuntimeCompatibility.Problems(targetFramework, "0.6.0").ShouldHaveSingleItem();

        problem.Code.ShouldBe(DamlDiagnosticCodes.RuntimeMismatch);
        problem.Message.ShouldBe(
            $"The project targets {targetFramework}, but the generated bindings use Daml.Runtime 0.6, which supports net10.0 and later only. Target net10.0 or later.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_project_without_the_runtime_is_told_which_one_to_add() =>
        RuntimeCompatibility.Problems("net10.0", null).ShouldHaveSingleItem().Message.ShouldBe(
            "The generated bindings need Daml.Runtime 0.6, which the project doesn't reference. Add <PackageReference Include=\"Daml.Runtime\" Version=\"0.6.0\" />.");

    [Theory, Trait("Category", "Unit")]
    [InlineData("0.5.2")]
    [InlineData("0.7.0")]
    [InlineData("1.0.0")]
    public void Another_runtime_line_doesnt_match_the_generator(string runtime) =>
        RuntimeCompatibility.Problems("net10.0", runtime).ShouldHaveSingleItem().Message.ShouldBe(
            $"The project references Daml.Runtime {runtime}, but the generator this release ships with writes code for Daml.Runtime 0.6. Use a 0.6 version of Daml.Runtime.");

    [Fact, Trait("Category", "Unit")]
    public void Both_problems_are_reported_at_once() =>
        RuntimeCompatibility.Problems("net48", "0.5.0").Count.ShouldBe(2);
}
