using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.Logging;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Logging;

public class DamlcLogAssemblerTests
{
    private static List<DamlcLogRecord> Assemble(params string[] lines)
    {
        var assembler = new DamlcLogAssembler();
        var records = lines.SelectMany(assembler.Add).ToList();
        if (assembler.Flush() is { } last)
        {
            records.Add(last);
        }
        return records;
    }

    // As damlc multi-ide 3.6.2 writes it: a blank line, a header with time and level, the text.
    [Fact, Trait("Category", "Unit")]
    public void Each_entry_becomes_one_line_without_damlcs_timestamp()
    {
        var records = Assemble(
            "",
            "2026-10-11 19:55:47.24 [INFO]  [Multi-IDE] ",
            "Updating package data",
            "",
            "2026-10-11 19:55:47.36 [INFO]  [Multi-IDE] ",
            "Added open file M:/repo/daml/TypeError.daml to M:/repo");

        records.ShouldBe([
            new DamlcLogRecord(DamlLogLevel.Information, "[Multi-IDE] Updating package data"),
            new DamlcLogRecord(DamlLogLevel.Information, "[Multi-IDE] Added open file M:/repo/daml/TypeError.daml to M:/repo"),
        ]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("DEBUG", DamlLogLevel.Debug)]
    [InlineData("INFO", DamlLogLevel.Information)]
    [InlineData("WARNING", DamlLogLevel.Warning)]
    [InlineData("ERROR", DamlLogLevel.Error)]
    [InlineData("TELEMETRY", DamlLogLevel.Information)]
    public void Entries_keep_damlcs_level(string level, DamlLogLevel expected) =>
        Assemble("", $"2026-10-11 19:55:47.24 [{level}]  [LanguageServer] ", "something").ShouldHaveSingleItem().Level.ShouldBe(expected);

    [Fact, Trait("Category", "Unit")]
    public void A_message_over_several_lines_stays_together()
    {
        var record = Assemble(
            "",
            "2026-10-11 20:00:21.92 [ERROR]  [LanguageServer ScriptService] ",
            "SCRIPT SERVICE STDERR: WARNING: A terminally deprecated method in sun.misc.Unsafe has been called",
            "WARNING: sun.misc.Unsafe::objectFieldOffset has been called").ShouldHaveSingleItem();

        record.Level.ShouldBe(DamlLogLevel.Warning); // a JVM warning, despite damlc's ERROR
        record.Text.ShouldBe("[LanguageServer ScriptService] SCRIPT SERVICE STDERR: WARNING: A terminally deprecated method in sun.misc.Unsafe has been called\nWARNING: sun.misc.Unsafe::objectFieldOffset has been called");
    }

    // As dpm build writes it: a diagnostic block follows an entry without a blank line.
    [Fact, Trait("Category", "Unit")]
    public void A_diagnostic_block_ends_the_entry_before_it_and_passes_through_line_by_line()
    {
        Assemble(
            "Running single package build of broken as no multi-package.yaml was found.",
            "",
            "2026-10-11 18:52:14.45 [INFO]  [build] ",
            "Compiling broken to a DAR.",
            "File:     daml/SyntaxError.daml",
            "Hidden:   no").Select(r => r.Text).ShouldBe([
                "Running single package build of broken as no multi-package.yaml was found.",
                "[build] Compiling broken to a DAR.",
                "File:     daml/SyntaxError.daml",
                "Hidden:   no",
            ]);
    }

    // damlc relays the script service's JVM warnings at ERROR level; they're warnings.
    [Fact, Trait("Category", "Unit")]
    public void Jvm_warnings_relayed_by_the_script_service_are_warnings()
    {
        Assemble(
            "",
            "2026-10-11 21:48:19.59 [ERROR]  [test ScriptService] ",
            "SCRIPT SERVICE STDERR: WARNING: A terminally deprecated method in sun.misc.Unsafe has been called",
            "",
            "2026-10-11 21:48:19.60 [ERROR]  [test ScriptService] ",
            "SCRIPT SERVICE STDERR: Exception in thread main")
            .Select(r => r.Level).ShouldBe([DamlLogLevel.Warning, DamlLogLevel.Error]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Text_on_the_header_line_itself_is_kept()
    {
        Assemble("2026-10-11 19:55:47.24 [WARNING] [Multi-IDE] Package lost").ShouldHaveSingleItem()
            .ShouldBe(new DamlcLogRecord(DamlLogLevel.Warning, "[Multi-IDE] Package lost"));
    }

    [Fact, Trait("Category", "Unit")]
    public void Lines_outside_any_entry_pass_through_at_once()
    {
        var assembler = new DamlcLogAssembler();

        assembler.Add("Some tool printed this").ShouldHaveSingleItem().ShouldBe(new DamlcLogRecord(DamlLogLevel.Information, "Some tool printed this"));
        assembler.HasPending.ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void An_entry_waits_for_its_end_until_flushed()
    {
        var assembler = new DamlcLogAssembler();

        assembler.Add("").ShouldBeEmpty();
        assembler.Add("2026-10-11 19:56:20.17 [INFO]  [Multi-IDE] ").ShouldBeEmpty();
        assembler.Add("Got new message from client").ShouldBeEmpty();

        assembler.HasPending.ShouldBeTrue();
        assembler.Flush().ShouldBe(new DamlcLogRecord(DamlLogLevel.Information, "[Multi-IDE] Got new message from client"));
        assembler.HasPending.ShouldBeFalse();
        assembler.Flush().ShouldBeNull();
    }
}
