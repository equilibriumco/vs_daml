using Equilibrium.Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Diagnostics;

public class ScriptResultLineTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.", "daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.")]
    [InlineData("daml/Main.daml:failing: failed", "daml/Main.daml:failing: failed")]
    [InlineData("daml/Main.daml:failing: failed\r", "daml/Main.daml:failing: failed")]
    [InlineData(@"daml\Asset\Test.daml:give_to'bob: ok, 0 active contracts, 0 transactions.  ", @"daml\Asset\Test.daml:give_to'bob: ok, 0 active contracts, 0 transactions.")]
    [InlineData("\u001b[0;32mdaml/Main.daml:setup: ok, 1 active contracts, 3 transactions.\u001b[0m", "daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.")]
    // dpm 1.1 sums up per package and per file, and names only the scripts that failed.
    [InlineData("Test Summary (single-package): 1 failed, 1 passed\r", "Test Summary (single-package): 1 failed, 1 passed")]
    [InlineData("Test Summary (single-package): 1 passed", "Test Summary (single-package): 1 passed")]
    [InlineData("daml/Main.daml: 1 test failed: failing", "daml/Main.daml: 1 test failed: failing")]
    [InlineData("daml/Main.daml: 2 tests failed: failing, other'one", "daml/Main.daml: 2 tests failed: failing, other'one")]
    [InlineData("daml/Main.daml: 1 test passed", "daml/Main.daml: 1 test passed")]
    [InlineData(@"daml\Asset\Test.daml: 12 tests passed  ", @"daml\Asset\Test.daml: 12 tests passed")]
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
    [InlineData("Running tests (single-package) ...")]
    [InlineData("Failed tests (passing tests omitted):")]
    [InlineData("daml/Main.daml: 1 test")]
    [InlineData("Test Summary (single-package)")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_lines_are_no_result(string? line) =>
        ScriptResultLine.TryParse(line).ShouldBeNull();
}
