using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Diagnostics;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Options;
using Equilibrium.Daml.VisualStudio.Core.Server;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Diagnostics;

// The file a user attaches to a bug report: versions, settings and the recent logs, masked.
public sealed class DiagnosticReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-report-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 7, 55, 0, TimeSpan.Zero));

    public DiagnosticReportTests() => Directory.CreateDirectory(Logs);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Logs => Path.Combine(_root, "logs");

    private string AddLog(string name, string content, TimeSpan age)
    {
        var path = Path.Combine(Logs, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, (_time.GetUtcNow() - age).UtcDateTime);
        return path;
    }

    private DiagnosticReportInput Input(long maxLogCharacters = 1024 * 1024) => new()
    {
        ExtensionVersion = "0.3.0",
        HostVersion = "18.0.11205.157",
        Settings = new DamlSettings
        {
            DpmPath = @"C:\Users\jdoe-private\tools\dpm.cmd",
            ExtraArguments = @"--access-token-file D:\clients\AcmeMergerSecret\token.jwt",
            LogLevel = ServerLogLevel.Debug,
        },
        LogFolder = Logs,
        MaxLogCharacters = maxLogCharacters,
    };

    private string Report(DiagnosticReportInput input) => DiagnosticReport.Create(input, DiagnosticRedactorTests.Redactor, _time);

    [Fact, Trait("Category", "Unit")]
    public void Nothing_planted_in_the_sensitive_fixture_reaches_the_report()
    {
        AddLog("daml-20261022-075013-4242.log", File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "diagnostics", "sensitive.log")), TimeSpan.FromMinutes(1));

        var report = Report(Input());

        foreach (var leak in new[]
        {
            "jdoe", "JDOE", "AcmeMergerSecret", "fileserver", "forty-two", "SecretTemplateBody", "31337", "secretCode",
            "1000000", "Mallory", "Alice", "9999999", "aaaaaaaa", "bbbbbbbb", "hunter2", "eyJhbGciOiJIUzI1NiJ9",
            "ghp_0123456789", "AKIAIOSFODNN7EXAMPLE", "acme-secret", "merger price", "ledger token",
        })
        {
            report.ShouldNotContain(leak, Case.Insensitive);
        }
        foreach (var kept in new[]
        {
            "Daml extension loaded (version 0.3.0)", "fail [LanguageServer] DAML0101", "daml/TypeError.daml:5:9: error:",
            @"${DPM}\cache\components\damlc\3.6.2\damlc-dist-dpm\damlc.exe", "Payout.daml", "merger-terms.dar",
            "Failed with status: UNHANDLED_EXCEPTION/DA.Exception.AssertionFailed", "stopped (exit code 1)",
        })
        {
            report.ShouldContain(kept);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void The_report_starts_with_versions_and_masked_settings()
    {
        var report = Report(Input());

        report.ShouldStartWith("EQ Daml Tools for Visual Studio diagnostics");
        report.ShouldContain("Read it before you share it");
        report.ShouldContain("Extension: 0.3.0");
        report.ShouldContain("Visual Studio: 18.0.11205.157");
        report.ShouldContain("Created: 2026-10-22");
        report.ShouldContain(@"DpmPath: ${HOME}\...\dpm.cmd");
        report.ShouldContain(@"ExtraArguments: --access-token-file <path>\...\token.jwt");
        report.ShouldContain("LogLevel: Debug");
        report.ShouldContain("UseMultiIde: True");
        report.ShouldContain("LiveBindingSync: False");
        report.ShouldNotContain("jdoe");
    }

    [Fact, Trait("Category", "Unit")]
    public void The_newest_logs_come_first_and_old_ones_are_left_out_past_the_size_limit()
    {
        AddLog("daml-20261020-000000-1.log", "oldest " + new string('o', 300) + "\r\n", TimeSpan.FromDays(2));
        AddLog("daml-20261021-000000-1.log", "older " + new string('m', 300) + "\r\n", TimeSpan.FromDays(1));
        AddLog("daml-20261022-000000-1.log", "first line\r\n" + new string('n', 300) + "\r\nlast line\r\n", TimeSpan.FromMinutes(1));
        AddLog("notes.txt", "not a log", TimeSpan.FromMinutes(1));

        // Exactly the two newest files.
        var report = Report(Input(maxLogCharacters: 325 + 308));

        report.IndexOf("daml-20261022-000000-1.log", StringComparison.Ordinal).ShouldBeLessThan(report.IndexOf("daml-20261021-000000-1.log", StringComparison.Ordinal));
        report.ShouldContain("last line");
        report.ShouldContain("older ");
        report.ShouldNotContain("oldest ");
        report.ShouldContain("daml-20261020-000000-1.log (left out: over the size limit)");
        report.ShouldNotContain("not a log");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_log_larger_than_what_is_left_keeps_its_end()
    {
        AddLog("daml-20261022-000000-1.log", "first line\r\n" + new string('n', 300) + "\r\nlast line\r\n", TimeSpan.FromMinutes(1));

        var report = Report(Input(maxLogCharacters: 100));

        report.ShouldContain("last line");
        report.ShouldNotContain("first line");
        report.ShouldContain("(the beginning is left out)");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Partial transaction:")]
    [InlineData("Committed transactions:")]
    [InlineData("Active contracts:")]
    [InlineData("Return value:")]
    [InlineData("Sub-transactions:")]
    [InlineData("Disclosures:")]
    [InlineData("Trace:")]
    public void Truncation_does_not_expose_result_continuations_after_the_header_is_left_out(string header)
    {
        const string Tail = "      with\r\n        amount = 9999999.0\r\n";
        const string Next = "next entry\r\n";
        AddLog("daml-20261022-000000-1.log",
            new string('x', 300) + "\r\n" + header + "\r\n  create Main:Merger\r\n" + Tail + Next, TimeSpan.Zero);

        // On the raw log this limit retains only Tail and Next, losing the result header.
        var report = Report(Input(maxLogCharacters: Tail.Length + Next.Length + 1));

        report.ShouldNotContain("9999999");
        report.ShouldNotContain("amount");
        report.ShouldNotContain("create Main:Merger");
        report.ShouldContain("<redacted script result data>");
        report.ShouldContain("next entry");
        report.ShouldContain("(the beginning is left out)");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Script_result_context_is_preserved_through_the_log_sink(bool separateEntries)
    {
        var sink = new LogFileSink(new RollingLogFile(Logs, 7, _time), _time);
        const string Result = "Partial transaction:\n  create Main:Merger\n    with\n      amount = 9999999.0";
        foreach (var entry in separateEntries ? Result.Split('\n') : new[] { Result })
        {
            sink.Write(DamlLogCategory.Build, DamlLogLevel.Information, entry);
        }
        sink.Write(DamlLogCategory.Build, DamlLogLevel.Information, "next entry");
        await sink.DrainAsync();

        var report = Report(Input());

        report.ShouldNotContain("9999999");
        report.ShouldNotContain("create Main:Merger");
        report.ShouldContain("Partial transaction:");
        report.ShouldContain("<redacted script result data>");
        report.ShouldContain("info [Build] next entry");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_missing_log_folder_is_reported_not_thrown()
    {
        var report = Report(Input() with { LogFolder = Path.Combine(_root, "none") });

        report.ShouldContain("No log files.");
    }

    [Fact, Trait("Category", "Unit")]
    public void Writing_puts_a_timestamped_file_in_the_folder_and_returns_its_path()
    {
        var folder = Path.Combine(_root, "diagnostics");

        var path = DiagnosticReport.Write(folder, Input(), DiagnosticRedactorTests.Redactor, _time);

        path.ShouldBe(Path.Combine(folder, "daml-diagnostics-20261022-075500.txt"));
        File.ReadAllText(path, Encoding.UTF8).ShouldStartWith("EQ Daml Tools for Visual Studio diagnostics");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_log_written_while_it_is_read_is_still_read()
    {
        var path = AddLog("daml-20261022-000000-1.log", "entry\r\n", TimeSpan.Zero);
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        Report(Input()).ShouldContain("entry");
    }

    [Fact, Trait("Category", "Unit")]
    public void Only_the_extensions_own_log_files_are_read()
    {
        AddLog("daml-20261022-000000-1.log", "own", TimeSpan.Zero);
        AddLog("daml-diagnostics-20261021-000000.txt", "an earlier export", TimeSpan.Zero);

        var report = Report(Input());

        report.ShouldContain("own");
        report.ShouldNotContain("an earlier export");
        report.Split('\n').Count(l => l.StartsWith("--- daml-", StringComparison.Ordinal)).ShouldBe(1);
    }
}
