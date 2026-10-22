using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Logging;

// Callers on any thread, the UI thread included, only queue an entry; the file is written on
// the thread pool, in order, and a failing disk never reaches the caller.
public sealed class LogFileSinkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "daml-sink-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 5, 50, 13, 250, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private LogFileSink Sink(string? folder = null)
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        return new LogFileSink(new RollingLogFile(folder ?? _folder, 7, _time), _time);
    }

    private string Content() => File.ReadAllText(RollingLogFile.Files(_folder).Single().FullName);

    [Fact, Trait("Category", "Unit")]
    public async Task Each_entry_is_one_line_with_local_time_level_and_category()
    {
        var sink = Sink();

        sink.Write(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, "The server is slow.");
        sink.Write(DamlLogCategory.Build, DamlLogLevel.Information, "Build succeeded.");
        await sink.DrainAsync();

        Content().ShouldBe(
            "2026-10-22 07:50:13.250 warn [LanguageServer] The server is slow.\r\n" +
            "2026-10-22 07:50:13.250 info [Build] Build succeeded.\r\n");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Line_breaks_inside_an_entry_become_the_file_line_ending()
    {
        var sink = Sink();

        sink.Write(DamlLogCategory.Extension, DamlLogLevel.Error, "failed\nat Foo()\r\nat Bar()");
        await sink.DrainAsync();

        Content().ShouldBe("2026-10-22 07:50:13.250 fail [Extension] failed\r\nat Foo()\r\nat Bar()\r\n");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Entries_from_many_threads_all_arrive_and_each_thread_keeps_its_order()
    {
        var sink = Sink();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                sink.Write(DamlLogCategory.Extension, DamlLogLevel.Information, $"t{thread} #{i:D3}");
            }
        })));
        await sink.DrainAsync();

        var lines = Content().Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1600);
        for (var thread = 0; thread < 8; thread++)
        {
            var own = lines.Where(l => l.Contains($" t{thread} #")).Select(l => l.Substring(l.Length - 3)).ToArray();
            own.ShouldBe(own.OrderBy(n => n, StringComparer.Ordinal).ToArray());
            own.Length.ShouldBe(200);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_folder_that_cannot_be_created_drops_the_entries_without_throwing()
    {
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "not-a-folder");
        File.WriteAllText(blocker, "");
        var sink = Sink(blocker);

        sink.Write(DamlLogCategory.Extension, DamlLogLevel.Information, "lost");
        await sink.DrainAsync();

        File.ReadAllText(blocker).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Draining_with_nothing_written_completes()
    {
        var sink = Sink();

        await sink.DrainAsync();

        Directory.Exists(_folder).ShouldBeFalse();
    }
}
