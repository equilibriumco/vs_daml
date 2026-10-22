using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.VisualStudio.Core.Build;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Build;

// Lines Visual Studio's Output window can jump from on double-click: path(line,column): ...
public class BuildDiagnosticOutputTests
{
    [Fact, Trait("Category", "Unit")]
    public void An_error_is_written_one_based_with_its_code()
    {
        var diagnostic = new BuildDiagnostic(@"C:\repo\daml\TypeError.daml", 4, 8, 4, 19, DamlDiagnosticSeverity.Error, "Couldn't match expected type ‘Int’ with actual type ‘Text’", "typecheck");

        diagnostic.Code.ShouldBe("EQD0001");
        diagnostic.ToOutputLine().ShouldBe(@"C:\repo\daml\TypeError.daml(5,9): error EQD0001: Couldn't match expected type ‘Int’ with actual type ‘Text’");
    }

    // damlc repeats the place in front of the text; the line already starts with it.
    [Theory, Trait("Category", "Unit")]
    [InlineData("daml/TypeError.daml:5:9: error:\n    • Couldn't match", "• Couldn't match")]
    [InlineData("daml/SyntaxError.daml:6:1: error:\n    parse error", "parse error")]
    [InlineData(@"C:\repo\daml\A.daml:1:2: warning: unused", "unused")]
    [InlineData("Could not find module `DA.DoesNotExist'", "Could not find module `DA.DoesNotExist'")]
    public void The_place_damlc_repeats_in_the_message_is_left_out(string message, string expected)
    {
        var diagnostic = new BuildDiagnostic(@"C:\repo\daml\X.daml", 0, 0, 0, 1, DamlDiagnosticSeverity.Error, message, null);

        diagnostic.ToOutputLine().ShouldBe(@"C:\repo\daml\X.daml(1,1): error EQD0001: " + expected);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_message_over_several_lines_stays_on_one()
    {
        var diagnostic = new BuildDiagnostic(@"C:\repo\Main.daml", 0, 0, 0, 1, DamlDiagnosticSeverity.Warning, "Unused import\n    • In module Main\r\n  ", null);

        diagnostic.Code.ShouldBe("EQD0002");
        diagnostic.ToOutputLine().ShouldBe(@"C:\repo\Main.daml(1,1): warning EQD0002: Unused import • In module Main");
    }
}
