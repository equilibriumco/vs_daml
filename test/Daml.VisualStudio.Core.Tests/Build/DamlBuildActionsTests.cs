using System.Linq;
using Daml.Build.Core;
using Daml.Build.Core.Diagnostics;
using Daml.VisualStudio.Core.Build;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Build;

public class DamlBuildActionsTests
{
    [Fact, Trait("Category", "Unit")]
    public void A_package_manifest_offers_build_test_and_clean_in_its_folder()
    {
        var actions = DamlBuildActions.For(@"C:\repo\app\daml.yaml");

        actions.Select(a => a.Kind).ShouldBe([DpmCommandKind.Build, DpmCommandKind.Test, DpmCommandKind.Clean]);
        actions.ShouldAllBe(a => a.WorkingDirectory == @"C:\repo\app" && !a.AllPackages);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_multi_package_manifest_builds_and_cleans_every_package_but_offers_no_test()
    {
        var actions = DamlBuildActions.For(@"C:\repo\multi-package.yaml");

        actions.Select(a => a.Kind).ShouldBe([DpmCommandKind.Build, DpmCommandKind.Clean]);
        actions.ShouldAllBe(a => a.WorkingDirectory == @"C:\repo" && a.AllPackages);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\app\DAML.YAML")]
    [InlineData(@"C:\repo\Multi-Package.yaml")]
    public void Manifest_names_match_regardless_of_case(string path) =>
        DamlBuildActions.For(path).ShouldNotBeEmpty();

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\app\daml\Main.daml")]
    [InlineData(@"C:\repo\app\not-daml.yaml")]
    [InlineData(@"C:\repo\app\daml.yaml.bak")]
    public void Other_files_offer_nothing(string path) =>
        DamlBuildActions.For(path).ShouldBeEmpty();

    [Fact, Trait("Category", "Unit")]
    public void Each_action_has_a_name_for_menus_and_logs()
    {
        DamlBuildActions.For(@"C:\repo\app\daml.yaml").Select(a => a.DisplayName).ShouldBe(["Build", "Test", "Clean"]);
        DamlBuildActions.For(@"C:\repo\multi-package.yaml").Select(a => a.DisplayName).ShouldBe(["Build All", "Clean All"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Diagnostic_ranges_become_zero_based_with_an_exclusive_end()
    {
        // damlc's 5:9-5:20 covers columns 9..19, 1-based.
        var diagnostic = new DamlDiagnostic
        {
            PrintedPath = "daml/TypeError.daml",
            Range = new DamlRange(5, 9, 5, 20),
            Severity = DamlDiagnosticSeverity.Error,
            Source = "typecheck",
            Message = "Couldn't match type",
        };

        var entry = BuildDiagnostic.From(diagnostic, @"C:\repo\broken");

        entry.ShouldBe(new BuildDiagnostic(@"C:\repo\broken\daml\TypeError.daml", 4, 8, 4, 19, DamlDiagnosticSeverity.Error, "Couldn't match type", "typecheck"));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_diagnostic_without_a_range_points_at_the_start_of_the_file()
    {
        var entry = BuildDiagnostic.From(new DamlDiagnostic { PrintedPath = "daml", Message = "No modules" }, @"C:\repo\app");

        (entry.Line, entry.Column, entry.EndLine, entry.EndColumn).ShouldBe((0, 0, 0, 0));
        entry.FilePath.ShouldBe(@"C:\repo\app\daml");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_zero_or_negative_position_never_becomes_negative()
    {
        var entry = BuildDiagnostic.From(new DamlDiagnostic { PrintedPath = "a.daml", Range = new DamlRange(0, 0, 0, 0) }, @"C:\repo");

        (entry.Line, entry.Column, entry.EndLine, entry.EndColumn).ShouldBe((0, 0, 0, 0));
    }
}
