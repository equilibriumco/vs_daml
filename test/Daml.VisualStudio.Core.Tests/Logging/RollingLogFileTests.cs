using System;
using System.IO;
using System.Linq;
using System.Text;
using Daml.VisualStudio.Core.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Logging;

// The extension's log files: one file per Visual Studio process until it reaches the size limit,
// and a folder that never holds more than the newest few files of the last week.
public sealed class RollingLogFileTests : IDisposable
{
    private const int ProcessId = 4242;
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 7, 50, 13, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "daml-logs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Start);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private RollingLogFile Log(long maxFileBytes = 1000, int maxFiles = 5, int retentionDays = 7) =>
        new(_folder, ProcessId, _time, new LogFileLimits { MaxFileBytes = maxFileBytes, MaxFiles = maxFiles, Retention = TimeSpan.FromDays(retentionDays) });

    private string[] Names() => Directory.GetFiles(_folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    private string OldLog(string name, TimeSpan age)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "old entry\r\n");
        File.SetLastWriteTimeUtc(path, (Start - age).UtcDateTime);
        return path;
    }

    [Fact, Trait("Category", "Unit")]
    public void The_defaults_are_five_files_of_ten_megabytes_kept_for_a_week()
    {
        LogFileLimits.Default.MaxFileBytes.ShouldBe(10L * 1024 * 1024);
        LogFileLimits.Default.MaxFiles.ShouldBe(5);
        LogFileLimits.Default.Retention.ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact, Trait("Category", "Unit")]
    public void By_default_the_files_live_with_the_extensions_other_local_data() =>
        RollingLogFile.DefaultFolder.ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamlVisualStudio", "logs"));

    [Fact, Trait("Category", "Unit")]
    public void Nothing_is_created_before_the_first_entry()
    {
        var log = Log();

        log.CurrentPath.ShouldBeNull();
        Directory.Exists(_folder).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void The_first_entry_creates_the_folder_and_a_file_named_after_the_time_and_the_process()
    {
        var log = Log();

        log.Append("first\r\n");
        log.Append("second\r\n");

        Names().ShouldBe(["daml-20261022-075013-4242.log"]);
        log.CurrentPath.ShouldBe(Path.Combine(_folder, "daml-20261022-075013-4242.log"));
        File.ReadAllText(log.CurrentPath!).ShouldBe("first\r\nsecond\r\n");
        File.ReadAllBytes(log.CurrentPath!).Take(3).ShouldNotBe(Encoding.UTF8.GetPreamble());
    }

    [Fact, Trait("Category", "Unit")]
    public void A_new_file_starts_when_the_next_entry_would_pass_the_size_limit()
    {
        var log = Log(maxFileBytes: 30);

        log.Append(new string('a', 20) + "\r\n");
        _time.Advance(TimeSpan.FromSeconds(5));
        log.Append(new string('b', 20) + "\r\n");

        Names().ShouldBe(["daml-20261022-075013-4242.log", "daml-20261022-075018-4242.log"]);
        File.ReadAllText(log.CurrentPath!).ShouldBe(new string('b', 20) + "\r\n");
    }

    [Fact, Trait("Category", "Unit")]
    public void An_entry_larger_than_the_limit_is_still_written_whole()
    {
        var log = Log(maxFileBytes: 10);
        var big = new string('x', 50) + "\r\n";

        log.Append(big);

        File.ReadAllText(log.CurrentPath!).ShouldBe(big);
    }

    [Fact, Trait("Category", "Unit")]
    public void Files_started_within_the_same_second_get_distinct_names()
    {
        var log = Log(maxFileBytes: 10);

        log.Append(new string('a', 10));
        log.Append(new string('b', 10));
        log.Append(new string('c', 10));

        Names().ShouldBe(["daml-20261022-075013-4242-1.log", "daml-20261022-075013-4242-2.log", "daml-20261022-075013-4242.log"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Only_the_newest_files_up_to_the_limit_stay()
    {
        for (var i = 1; i <= 6; i++)
        {
            OldLog($"daml-2026102{i}-000000-1.log", TimeSpan.FromHours(10 - i));
        }

        Log(maxFiles: 5).Append("entry\r\n");

        // The current file and the four newest older ones.
        Names().ShouldBe(
        [
            "daml-20261023-000000-1.log", "daml-20261024-000000-1.log", "daml-20261025-000000-1.log",
            "daml-20261026-000000-1.log", "daml-20261022-075013-4242.log",
        ], ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public void Files_older_than_the_retention_period_are_deleted()
    {
        OldLog("daml-20261014-000000-1.log", TimeSpan.FromDays(8));
        OldLog("daml-20261016-000000-1.log", TimeSpan.FromDays(6));

        Log().Append("entry\r\n");

        Names().ShouldBe(["daml-20261016-000000-1.log", "daml-20261022-075013-4242.log"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Old_files_go_when_a_new_file_starts_too()
    {
        var log = Log(maxFileBytes: 10);
        log.Append(new string('a', 10));
        File.SetLastWriteTimeUtc(log.CurrentPath!, Start.UtcDateTime);
        OldLog("daml-20261017-000000-1.log", TimeSpan.FromDays(5));
        _time.Advance(TimeSpan.FromDays(3));

        log.Append(new string('b', 10));

        Names().ShouldBe(["daml-20261022-075013-4242.log", "daml-20261025-075013-4242.log"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Other_files_in_the_folder_are_left_alone()
    {
        Directory.CreateDirectory(_folder);
        foreach (var name in new[] { "notes.txt", "daml-diagnostics-20261001-000000.txt", "damlc.log" })
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllText(path, "keep");
            File.SetLastWriteTimeUtc(path, (Start - TimeSpan.FromDays(30)).UtcDateTime);
        }

        Log(maxFiles: 1).Append("entry\r\n");

        Names().ShouldBe(["daml-20261022-075013-4242.log", "daml-diagnostics-20261001-000000.txt", "damlc.log", "notes.txt"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_file_deleted_from_under_it_is_started_again()
    {
        var log = Log();
        log.Append("before\r\n");
        File.Delete(log.CurrentPath!);

        log.Append("after\r\n");

        File.ReadAllText(log.CurrentPath!).ShouldBe("after\r\n");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_folder_deleted_from_under_it_is_created_again()
    {
        var log = Log();
        log.Append("before\r\n");
        Directory.Delete(_folder, recursive: true);

        log.Append("after\r\n");

        File.ReadAllText(log.CurrentPath!).ShouldBe("after\r\n");
    }

    [Fact, Trait("Category", "Unit")]
    public void The_files_are_listed_newest_first()
    {
        OldLog("daml-20261020-000000-1.log", TimeSpan.FromDays(2));
        OldLog("daml-20261021-000000-1.log", TimeSpan.FromDays(1));
        OldLog("unrelated.log", TimeSpan.FromHours(1));
        OldLog("daml-20261022-000000-1.logx", TimeSpan.FromHours(1));

        RollingLogFile.Files(_folder).Select(f => f.Name).ShouldBe(["daml-20261021-000000-1.log", "daml-20261020-000000-1.log"]);
        RollingLogFile.Files(Path.Combine(_folder, "missing")).ShouldBeEmpty();
    }
}
