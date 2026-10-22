using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Diagnostics;

public class DamlDiagnosticParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "build-output", name));

    [Fact, Trait("Category", "Unit")]
    public void A_failed_build_yields_one_diagnostic_per_block()
    {
        var diagnostics = DamlDiagnosticParser.Parse(Fixture("build-broken.stderr.txt"));

        diagnostics.Select(d => d.PrintedPath).ShouldBe(["daml/SyntaxError.daml", "daml/TypeError.daml", "daml/UnknownImport.daml"]);
        diagnostics.ShouldAllBe(d => d.Severity == DamlDiagnosticSeverity.Error && !d.Hidden && d.PackageRoot == null);

        var type = diagnostics[1];
        type.Range.ShouldBe(new DamlRange(5, 9, 5, 20));
        type.Source.ShouldBe("typecheck");
        type.Message.ShouldBe(
            "daml/TypeError.daml:5:9: error:\n" +
            "    • Couldn't match expected type ‘Int’ with actual type ‘Text’\n" +
            "    • In the expression: \"forty-two\"\n" +
            "      In an equation for ‘total’: total = \"forty-two\"");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_message_ends_where_dpm_reports_the_failure()
    {
        var unknownImport = DamlDiagnosticParser.Parse(Fixture("build-broken.stderr.txt"))[2];

        unknownImport.Source.ShouldBe("not found");
        unknownImport.Range.ShouldBe(new DamlRange(4, 8, 4, 23));
        unknownImport.Message.ShouldBe("Could not find module ‘DA.DoesNotExist’\nIt is not a module in the current program, or in any known package.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_parse_error_at_the_end_of_the_file_points_past_its_last_line()
    {
        // SyntaxError.daml has five lines; 1-based line 6 is just past them.
        DamlDiagnosticParser.Parse(Fixture("build-broken.stderr.txt"))[0].Range.ShouldBe(new DamlRange(6, 1, 6, 1));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_package_level_warning_keeps_its_multi_line_message_and_stops_at_the_blank_line()
    {
        var warning = DamlDiagnosticParser.Parse(Fixture("build-single-package.stderr.txt")).ShouldHaveSingleItem();

        warning.PrintedPath.ShouldBe("daml");
        warning.Severity.ShouldBe(DamlDiagnosticSeverity.Warning);
        warning.Source.ShouldBe("Daml-LF typechecker");
        warning.Message.ShouldStartWith("warning while type checking package:\n  This package defines templates");
        warning.Message.ShouldEndWith("Disable this warning entirely with -Wno-template-interface-depends-on-daml-script");
    }

    [Fact, Trait("Category", "Unit")]
    public void In_a_multi_package_build_files_belong_to_the_package_being_built()
    {
        var diagnostics = DamlDiagnosticParser.Parse(Fixture("build-multi-package-broken.stderr.txt"));

        diagnostics.Count.ShouldBe(4);
        diagnostics.Take(3).ShouldAllBe(d => d.PackageRoot == @"C:\work\multi-broken\broken");
        diagnostics[0].ResolvePath(@"C:\work\multi-broken").ShouldBe(@"C:\work\multi-broken\broken\daml\SyntaxError.daml");

        var package = diagnostics[3];
        package.PrintedPath.ShouldBe("C:/work/multi-broken/broken");
        package.ResolvePath(@"C:\work\multi-broken").ShouldBe(@"C:\work\multi-broken\broken");
        package.Source.ShouldBe("compiler");
        package.Message.ShouldStartWith("Failed to build package at C:/work/multi-broken/broken.\nCallStack (from HasCallStack):");
        package.Message.ShouldNotContain("Shake");
    }

    [Fact, Trait("Category", "Unit")]
    public void Test_runs_report_the_same_blocks_between_log_lines()
    {
        var diagnostics = DamlDiagnosticParser.Parse(Fixture("test-broken.stderr.txt"));

        diagnostics.Select(d => d.PrintedPath).ShouldBe(["daml/UnknownImport.daml", "daml/SyntaxError.daml"]);
        diagnostics[1].Message.ShouldBe("daml/SyntaxError.daml:6:1: error:\n    parse error (possibly incorrect indentation or mismatched brackets)");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("test-single-package.stdout.txt")]
    [InlineData("test-single-package.stderr.txt")]
    [InlineData("clean-single-package.stderr.txt")]
    public void Output_without_blocks_has_no_diagnostics(string fixture) =>
        DamlDiagnosticParser.Parse(Fixture(fixture)).ShouldBeEmpty();

    [Fact, Trait("Category", "Unit")]
    public void Lines_fed_one_at_a_time_give_the_same_result_as_the_whole_text()
    {
        var text = Fixture("build-multi-package-broken.stderr.txt");
        var parser = new DamlDiagnosticParser();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            parser.AddLine(line);
        }
        parser.Complete();

        parser.Diagnostics.ShouldBe(DamlDiagnosticParser.Parse(text));
    }

    [Fact, Trait("Category", "Unit")]
    public void Line_endings_do_not_matter()
    {
        var text = Fixture("build-broken.stderr.txt");

        DamlDiagnosticParser.Parse(text.Replace("\r\n", "\n")).ShouldBe(DamlDiagnosticParser.Parse(text.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n")));
    }

    [Fact, Trait("Category", "Unit")]
    public void Colour_codes_damlc_writes_into_messages_are_dropped()
    {
        var colored = DamlDiagnosticParser.Parse("File: a.daml\nMessage: \n\u001b[0;91mfirst\nsecond\u001b[0m\n").ShouldHaveSingleItem();

        colored.Message.ShouldBe("first\nsecond");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_block_cut_off_at_the_end_is_kept_with_what_it_has()
    {
        var diagnostics = DamlDiagnosticParser.Parse("File:     daml/A.daml\nHidden:   no\nRange:    2:3-2:7\n");

        var cut = diagnostics.ShouldHaveSingleItem();
        cut.Range.ShouldBe(new DamlRange(2, 3, 2, 7));
        cut.Severity.ShouldBe(DamlDiagnosticSeverity.Error);
        cut.Message.ShouldBe("");
    }

    [Fact, Trait("Category", "Unit")]
    public void Unknown_fields_and_odd_values_do_not_break_a_block()
    {
        var diagnostics = DamlDiagnosticParser.Parse(
            "noise before\nFile:     C:\\repo\\daml\\B.daml\nHidden:   yes\nRange:    not-a-range\nFlavour:  new\nSource:   x\nSeverity: DsInfo\nMessage: one line\n");

        var odd = diagnostics.ShouldHaveSingleItem();
        odd.PrintedPath.ShouldBe(@"C:\repo\daml\B.daml");
        odd.Hidden.ShouldBeTrue();
        odd.Range.ShouldBeNull();
        odd.Severity.ShouldBe(DamlDiagnosticSeverity.Information);
        odd.Message.ShouldBe("one line");
        odd.ResolvePath(@"D:\elsewhere").ShouldBe(@"C:\repo\daml\B.daml");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("DsError", DamlDiagnosticSeverity.Error)]
    [InlineData("DsWarning", DamlDiagnosticSeverity.Warning)]
    [InlineData("DsInfo", DamlDiagnosticSeverity.Information)]
    [InlineData("DsHint", DamlDiagnosticSeverity.Hint)]
    [InlineData("Something", DamlDiagnosticSeverity.Error)]
    public void Severities_map_from_the_damlc_names(string value, DamlDiagnosticSeverity expected) =>
        DamlDiagnosticParser.Parse($"File: a.daml\nSeverity: {value}\nMessage: m\n").ShouldHaveSingleItem().Severity.ShouldBe(expected);

    [Fact, Trait("Category", "Unit")]
    public void A_block_without_a_real_file_is_kept_as_printed()
    {
        var diagnostics = DamlDiagnosticParser.Parse("File:     <unknown>\nHidden:   no\nRange:    1:1-1:1\nSource:   package\nSeverity: DsError\nMessage:\nMissing dependency daml-script\n");

        diagnostics.ShouldHaveSingleItem().PrintedPath.ShouldBe("<unknown>");
        diagnostics[0].Message.ShouldBe("Missing dependency daml-script");
    }

    [Fact, Trait("Category", "Unit")]
    public void An_indented_line_after_a_blank_line_continues_the_message()
    {
        var output = "File:     daml/A.daml\nHidden:   no\nRange:    1:1-1:2\nSource:   typecheck\nSeverity: DsError\nMessage:\ndaml/A.daml:1:1: error:\n    Variable not in scope: foo\n\n    Perhaps you meant 'for'\n\nBuilding C:/work/b\nFile:     daml/B.daml\nHidden:   no\nRange:    2:1-2:2\nSource:   parser\nSeverity: DsError\nMessage:\nparse error\n";

        var diagnostics = DamlDiagnosticParser.Parse(output);

        diagnostics.Count.ShouldBe(2);
        diagnostics[0].Message.ShouldBe("daml/A.daml:1:1: error:\n    Variable not in scope: foo\n\n    Perhaps you meant 'for'");
        diagnostics[1].PackageRoot.ShouldBe(@"C:\work\b");
    }

    [Fact, Trait("Category", "Unit")]
    public void Lines_inside_a_block_that_arent_fields_are_reported_not_dropped()
    {
        var parser = new DamlDiagnosticParser();
        foreach (var line in new[] { "File:     daml/A.daml", "Hidden:   no", "Weight:   heavy", "Severity: DsError", "Message:", "boom" })
        {
            parser.AddLine(line);
        }
        parser.Complete();

        parser.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe("boom");
        parser.UnrecognisedLines.ShouldBe(["Weight:   heavy"]);
    }
}
