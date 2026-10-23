using System;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Building;

public class DpmOutputReportTests
{
    private const string Root = @"C:\work\broken";
    private const string Manifest = Root + @"\daml.yaml";

    private static readonly ToolOptions Online = new(null, Offline: false, AllowToolInstall: false, SkipPreflight: false, null, TimeSpan.FromSeconds(600));
    private static readonly ToolOptions Offline = Online with { Offline = true };

    [Fact, Trait("Category", "Unit")]
    public void Failure_tails_never_expose_URL_credentials()
    {
        var outcome = Report("Unable to fetch oci://alice:secret@registry/contracts\n", 1);
        outcome.Messages.ShouldHaveSingleItem().Text.ShouldContain("oci://***@registry/contracts");
        outcome.Messages.ShouldAllBe(m => !m.Text.Contains("secret") && !m.Text.Contains("alice"));
    }

    private static DpmOutcome Report(string output, int exitCode, ToolOptions? options = null, string root = Root, DpmCommandKind kind = DpmCommandKind.Build)
    {
        var report = new DpmOutputReport(kind, root, root + @"\daml.yaml", options ?? Online);
        foreach (var line in output.Split('\n'))
        {
            report.AddLine(line);
        }
        return report.Complete(exitCode);
    }

    private static string Captured(string name) =>
        File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "build-output", name));

    [Fact, Trait("Category", "Unit")]
    public void Compile_errors_are_EQD0001_at_their_file_and_range()
    {
        var outcome = Report(Captured("build-broken.stderr.txt"), exitCode: 1);

        outcome.Messages.Select(m => (m.Severity, m.Code, m.Subcategory, m.File, m.Line, m.Column, m.EndLine, m.EndColumn)).ShouldBe(
        [
            (DamlDiagnosticSeverity.Error, "EQD0001", "parser", Root + @"\daml\SyntaxError.daml", 6, 1, 6, 1),
            (DamlDiagnosticSeverity.Error, "EQD0001", "typecheck", Root + @"\daml\TypeError.daml", 5, 9, 5, 20),
            (DamlDiagnosticSeverity.Error, "EQD0001", "not found", Root + @"\daml\UnknownImport.daml", 4, 8, 4, 23),
        ]);
        outcome.Messages[1].Text.ShouldBe(
            "• Couldn't match expected type ‘Int’ with actual type ‘Text’ • In the expression: \"forty-two\" In an equation for ‘total’: total = \"forty-two\"");
        outcome.CreatedDar.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_clean_build_names_its_DAR_and_keeps_its_warnings()
    {
        var outcome = Report(Captured("build-single-package.stderr.txt"), exitCode: 0, root: @"C:\work\single-package");

        var warning = outcome.Messages.ShouldHaveSingleItem();
        (warning.Severity, warning.Code, warning.File, warning.Line).ShouldBe((DamlDiagnosticSeverity.Warning, "EQD0002", @"C:\work\single-package\daml", 1));
        warning.Text.ShouldStartWith("warning while type checking package: This package defines templates or interfaces");
        outcome.CreatedDar.ShouldBe(@"C:\work\single-package\.daml\dist\single-package-0.0.1.dar");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_failing_script_is_an_error_at_the_script()
    {
        var outcome = Report(Captured("test-failing-script.stderr.txt") + Captured("test-failing-script.stdout.txt"), exitCode: 1,
            root: @"C:\work\test-fail", kind: DpmCommandKind.Test);

        var error = outcome.Messages.ShouldHaveSingleItem();
        (error.Code, error.Subcategory, error.File, error.Line, error.Column).ShouldBe(("EQD0001", "Script", @"C:\work\test-fail\daml\Main.daml", 47, 1));
        error.Text.ShouldStartWith("Script execution failed: Failed with status: UNHANDLED_EXCEPTION/DA.Exception.AssertionFailed:AssertionFailed: the total is wrong");
    }

    private const string SdkMissing =
        "SDK_NOT_INSTALLED: target dpm-sdk version not installed. You can install the needed sdk (for \"C:\\\\work\\\\broken\\\\daml.yaml\") via 'dpm install 3.4.0'";

    [Fact, Trait("Category", "Unit")]
    public void A_missing_SDK_is_EQD0101_with_the_way_to_get_it()
    {
        var error = Report(SdkMissing, exitCode: 1).Messages.ShouldHaveSingleItem();

        (error.Code, error.File, error.Line).ShouldBe(("EQD0101", Manifest, 0));
        error.Text.ShouldBe(SdkMissing + " Install it with that command, or set EqDamlAllowToolInstall to true to let the build install it.");
    }

    [Fact, Trait("Category", "Unit")]
    public void Offline_a_missing_SDK_is_EQD0107()
    {
        var error = Report(SdkMissing, exitCode: 1, Offline).Messages.ShouldHaveSingleItem();

        error.Code.ShouldBe("EQD0107");
        error.Text.ShouldBe(SdkMissing + " EqDamlOffline is true, so the build installs nothing; install it beforehand with that command.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_failed_install_is_EQD0101_with_dpms_reason()
    {
        const string Line = "error while auto-installing sdk version \"3.4.0\": Get \"https://europe-docker.pkg.dev/v2/x\": proxyconnect tcp: dial tcp 127.0.0.1:9: connectex: refused.";

        var error = Report(Line, exitCode: 1, Online with { AllowToolInstall = true }).Messages.ShouldHaveSingleItem();

        (error.Code, error.Text).ShouldBe(("EQD0101", Line));
    }

    // Without anything recognisable, the end of the output is all there is to go on.
    [Fact, Trait("Category", "Unit")]
    public void An_unexplained_failure_shows_the_end_of_the_output()
    {
        var output = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"line {i}")) + "\n\n";

        var error = Report(output, exitCode: 3).Messages.ShouldHaveSingleItem();

        (error.Code, error.File).ShouldBe(("EQD0001", Manifest));
        error.Text.ShouldBe("dpm build failed with exit code 3:\n" + string.Join("\n", Enumerable.Range(6, 10).Select(i => $"line {i}")));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_failure_already_explained_by_diagnostics_adds_nothing() =>
        Report(Captured("test-broken.stderr.txt"), exitCode: 1, kind: DpmCommandKind.Test).Messages.ShouldAllBe(m => m.Code == "EQD0001" && m.Subcategory != null);
}
