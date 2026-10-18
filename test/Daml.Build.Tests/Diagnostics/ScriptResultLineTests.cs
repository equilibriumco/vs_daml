using Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Diagnostics;

public class ScriptResultLineTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.", "daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.")]
    [InlineData("daml/Main.daml:failing: failed", "daml/Main.daml:failing: failed")]
    [InlineData("daml/Main.daml:failing: failed\r", "daml/Main.daml:failing: failed")]
    [InlineData(@"daml\Asset\Test.daml:give_to'bob: ok, 0 active contracts, 0 transactions.  ", @"daml\Asset\Test.daml:give_to'bob: ok, 0 active contracts, 0 transactions.")]
    [InlineData("\u001b[0;32mdaml/Main.daml:setup: ok, 1 active contracts, 3 transactions.\u001b[0m", "daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.")]
    public void The_summary_line_of_a_script_is_its_result(string line, string result) =>
        ScriptResultLine.TryParse(line).ShouldBe(result);

    [Theory, Trait("Category", "Unit")]
    [InlineData("Test Summary\u001b[m")]
    [InlineData("Modules internal to this package:")]
    [InlineData("  1 (100.0%) created")]
    [InlineData("File:     daml/Probe.daml")]
    [InlineData("daml/Main.daml:47:1: error:")]
    [InlineData("Created .daml/dist/contracts-0.1.0.dar")]
    [InlineData("  daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.")]
    [InlineData("daml/Main.daml:setup: okay")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_lines_are_no_result(string? line) =>
        ScriptResultLine.TryParse(line).ShouldBeNull();
}
