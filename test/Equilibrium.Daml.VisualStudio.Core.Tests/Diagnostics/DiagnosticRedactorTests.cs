using Equilibrium.Daml.VisualStudio.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Diagnostics;

// What a diagnostic export masks, rule by rule. The report goes to people outside the machine,
// so when in doubt a rule masks too much rather than too little.
public class DiagnosticRedactorTests
{
    internal static readonly DiagnosticRedactor Redactor = new(
        [
            new RedactedFolder(@"C:\Users\jdoe-private\AppData\Roaming\dpm", "${DPM}", KeepRest: true),
            new RedactedFolder(@"C:\Users\jdoe-private\AppData\Local\EquilibriumDamlTools", "${EXTENSIONDATA}", KeepRest: true),
            new RedactedFolder(@"C:\Users\jdoe-private\AppData\Local\Temp", "${TEMP}", KeepRest: false),
            new RedactedFolder(@"C:\Users\jdoe-private\AppData\Local", "${LOCALAPPDATA}", KeepRest: false),
            new RedactedFolder(@"C:\Users\jdoe-private", "${HOME}", KeepRest: false),
            new RedactedFolder(@"C:\Users\jdoe-private\source\repos\AcmeMergerSecret", "${WORKSPACE}", KeepRest: false),
        ],
        [("jdoe-private", "<user>"), ("JDOE-WORKSTATION", "<machine>")]);

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"in C:\Users\jdoe-private\source\repos\AcmeMergerSecret\daml\Main.daml now", @"in ${WORKSPACE}\...\Main.daml now")]
    [InlineData(@"in C:/Users/jdoe-private/source/repos/AcmeMergerSecret/daml/Main.daml", @"in ${WORKSPACE}/.../Main.daml")]
    [InlineData(@"open file:///c%3A/users/JDOE-PRIVATE/source/repos/AcmeMergerSecret/daml/Main.daml", @"open file:///${WORKSPACE}/.../Main.daml")]
    [InlineData(@"folder C:\Users\jdoe-private\source\repos\AcmeMergerSecret", @"folder ${WORKSPACE}")]
    [InlineData(@"tool C:\Users\jdoe-private\AppData\Roaming\dpm\cache\components\damlc\3.6.2\damlc.exe", @"tool ${DPM}\cache\components\damlc\3.6.2\damlc.exe")]
    [InlineData(@"cache C:\Users\jdoe-private\AppData\Local\EquilibriumDamlTools\codegen\sha256-0f", @"cache ${EXTENSIONDATA}\codegen\sha256-0f")]
    [InlineData(@"copy C:\Users\jdoe-private\AppData\Local\Temp\daml-copy-8812\Main.daml", @"copy ${TEMP}\...\Main.daml")]
    [InlineData(@"D:\clients\AcmeMergerSecret\daml\Payout.daml:3:1: error", @"<path>\...\Payout.daml:3:1: error")]
    [InlineData(@"""D:\clients\AcmeMergerSecret\ledger token.jwt""", @"""<path> token.jwt""")]
    [InlineData(@"file:///d%3A/clients/AcmeMergerSecret/daml/Payout.daml", @"file:///<path>/.../Payout.daml")]
    [InlineData(@"DAR \\fileserver\AcmeMergerSecret\dars\merger-terms.dar", @"DAR <path>\...\merger-terms.dar")]
    [InlineData(@"in D:\clients\AcmeMergerSecret", @"in <path>")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", @"<path>\...\dotnet.exe")]
    public void Paths_keep_only_a_placeholder_and_the_file_name(string text, string expected) =>
        Redactor.Redact(text).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData("daml/Main.daml:12:3: error")]
    [InlineData("Range:    6:1-6:1")]
    [InlineData("https://docs.digitalasset.com/build/3.4/index.html")]
    [InlineData("Main:Asset at 07:50:13.250")]
    [InlineData("${WORKSPACE}\\daml")]
    public void Things_that_only_look_like_paths_stay(string text) =>
        Redactor.Redact(text).ShouldBe(text);

    [Fact, Trait("Category", "Unit")]
    public void The_user_and_machine_names_are_masked_wherever_they_stand_alone() =>
        Redactor.Redact("opened by JDoe-Private on jdoe-workstation; jdoe-privateer stays").ShouldBe("opened by <user> on <machine>; jdoe-privateer stays");

    [Fact, Trait("Category", "Unit")]
    public void Compiler_excerpts_and_expressions_are_masked()
    {
        var text = """
            daml/TypeError.daml:5:9: error:
                • Couldn't match expected type ‘Int’ with actual type ‘Text’
                • In the expression: "forty-two-secret-literal"
                  In an equation for ‘total’: total = "forty-two-secret-literal"
               |
            12 |   payout = SecretTemplateBody with ceoBonus = 31337
               |            ^^^^^^^^^^^^^^^^^^
            """;

        var redacted = Redactor.Redact(text);

        redacted.ShouldNotContain("forty-two");
        redacted.ShouldNotContain("SecretTemplateBody");
        redacted.ShouldNotContain("31337");
        redacted.ShouldContain("daml/TypeError.daml:5:9: error:");
        redacted.ShouldContain("Couldn't match expected type ‘Int’ with actual type ‘Text’");
        redacted.ShouldContain("• In the expression: <redacted code>");
        redacted.ShouldContain("In an equation for ‘total’: <redacted code>");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("12 |   payout = SecretTemplateBody with ceoBonus = 31337")]
    [InlineData("   |            ^^^^^^^^^^^^^^^^^^")]
    [InlineData("    • In the expression: \"forty-two-secret-literal\"")]
    [InlineData("      In an equation for ‘total’: total = \"forty-two-secret-literal\"")]
    public void Compiler_code_is_masked_inside_timestamped_and_flattened_lines(string code)
    {
        const string Prefix = "2026-10-22 07:50:15.000 info [Build] ";
        const string Diagnostic = "daml/Main.daml(12,3): error EQD0001: Type mismatch ";
        foreach (var prefix in new[] { "", Prefix, Diagnostic, Prefix + Diagnostic })
        {
            var redacted = Redactor.Redact(prefix + code);

            redacted.ShouldStartWith(prefix);
            redacted.ShouldContain("<redacted code>");
            redacted.ShouldNotContain("SecretTemplateBody");
            redacted.ShouldNotContain("31337");
            redacted.ShouldNotContain("forty-two");
            redacted.ShouldNotContain("total =");
            redacted.ShouldNotContain("^^^^^^^^");
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void Protocol_payloads_are_replaced_by_their_length()
    {
        var redacted = Redactor.Redact("""Sent {"method":"textDocument/didOpen","params":{"textDocument":{"uri":"x","text":"module Main where\nsecretCode = 1"}}}""");

        redacted.ShouldNotContain("secretCode");
        redacted.ShouldContain("\"text\":\"<redacted 32 chars>\"");
        redacted.ShouldContain("\"uri\":\"x\"");
    }

    [Fact, Trait("Category", "Unit")]
    public void Script_results_are_masked_but_the_failure_kind_stays()
    {
        var text = """
            Script execution failed:
              Failed with status: UNHANDLED_EXCEPTION/DA.Exception.AssertionFailed:AssertionFailed: payout 1000000 to Bob is wrong
            Script execution failed on commit at Main:39:3:
              1: create of Main:Merger at unknown source
                 failed due to a missing authorization from 'Mallory'

            Partial transaction:
              Sub-transactions:
                 #0
                 └─> create Main:Merger
                     with
                       acquirer = 'Alice'; price = 9999999.0
            2026-10-22 07:50:16.400 info [Build] next entry
            """;

        var redacted = Redactor.Redact(text);

        redacted.ShouldNotContain("1000000");
        redacted.ShouldNotContain("Mallory");
        redacted.ShouldNotContain("Alice");
        redacted.ShouldNotContain("9999999");
        redacted.ShouldContain("Failed with status: UNHANDLED_EXCEPTION/DA.Exception.AssertionFailed: <redacted result>");
        redacted.ShouldContain("Partial transaction:");
        redacted.ShouldContain("<redacted script result data>");
        redacted.ShouldContain("2026-10-22 07:50:16.400 info [Build] next entry");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("party Alice::1220aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa here", "party <party> here")]
    [InlineData("cid 00bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbcafe archived", "cid <contract id> archived")]
    [InlineData("package 867c8b7e7e8feb98f0bb71c2c86772f20124a2dc1607e0139e5713dcdc76b385 (daml-script)", "package 867c8b7e7e8feb98f0bb71c2c86772f20124a2dc1607e0139e5713dcdc76b385 (daml-script)")]
    public void Party_and_contract_ids_are_masked_but_package_ids_stay(string text, string expected) =>
        Redactor.Redact(text).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData("dpm couldn't reach https://jdoe:hunter2pass@registry.example.com/v2/", "hunter2pass")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("DPM_REGISTRY_PASSWORD=hunter2-very-secret", "hunter2")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ012345 in the environment", "ghp_0123456789")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE key", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("contact jdoe@acme-secret.example", "acme-secret")]
    public void Secrets_are_masked(string text, string leak) =>
        Redactor.Redact(text).ShouldNotContain(leak);

    [Fact, Trait("Category", "Unit")]
    public void Ordinary_log_lines_stay_as_they_are()
    {
        const string Line = "2026-10-22 07:50:15.000 fail [LanguageServer] EQD0101: dpm is not installed. Install it, or set the dpm path in Tools > Options > EQ Daml Tools.";

        Redactor.Redact(Line).ShouldBe(Line);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_current_user_profile_is_among_the_masked_folders()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        DiagnosticRedactor.ForCurrentUser([]).Redact($@"{home}\secret-project\x.daml").ShouldBe(@"${HOME}\...\x.daml");
    }

    // What Export Diagnostics uses, with this machine's own folders and names: nothing is changed
    // in the process's environment, which other tests share.
    [Fact, Trait("Category", "Unit")]
    public void This_users_folders_keep_the_layout_that_helps_and_lose_the_rest()
    {
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var roaming = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var temp = System.IO.Path.GetTempPath().TrimEnd('\\');
        var redactor = DiagnosticRedactor.ForCurrentUser([@"Q:\clients\acme\app"]);

        redactor.Redact($@"{local}\EquilibriumDamlTools\logs\daml-1.log").ShouldBe(@"${EXTENSIONDATA}\logs\daml-1.log");
        redactor.Redact($@"{roaming}\dpm\cache\sdk\3.6.0\damlc.exe").ShouldBe(@"${DPM}\cache\sdk\3.6.0\damlc.exe");
        redactor.Redact($@"{temp}\daml-build-123\Main.daml").ShouldBe(@"${TEMP}\...\Main.daml");
        redactor.Redact($@"{local}\SomeOtherTool\cache.db").ShouldBe(@"${LOCALAPPDATA}\...\cache.db");
        redactor.Redact($@"{roaming}\SomeOtherTool\settings.json").ShouldBe(@"${APPDATA}\...\settings.json");
        // The workspace may live outside the profile; its own path is the user's business too.
        redactor.Redact(@"Q:\clients\acme\app\daml\Main.daml").ShouldBe(@"${WORKSPACE}\...\Main.daml");
    }

    [Fact, Trait("Category", "Unit")]
    public void This_users_and_this_machines_names_are_masked()
    {
        var user = System.Environment.UserName;
        var machine = System.Environment.MachineName;
        var redacted = DiagnosticRedactor.ForCurrentUser([]).Redact($"signed in as {user} on {machine}.");

        // Names shorter than three letters stay: they would mask ordinary words.
        redacted.ShouldBe($"signed in as {(user.Length >= 3 ? "<user>" : user)} on {(machine.Length >= 3 ? "<machine>" : machine)}.");
    }
}
