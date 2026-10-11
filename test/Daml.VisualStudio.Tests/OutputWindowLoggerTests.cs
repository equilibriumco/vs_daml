using System;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Logging;
using Microsoft.VisualStudio.Sdk.TestFramework;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

[Collection(MockedVS.Collection)]
public class OutputWindowLoggerTests
{
    private readonly FakeOutputWindow _outputWindow = new();

    public OutputWindowLoggerTests(GlobalServiceProvider services)
    {
        services.Reset();
        services.AddService(typeof(SVsOutputWindow), _outputWindow);
    }

    // Writes are coalesced, so one pane write can hold several lines; tests look at the lines.
    private static string[] LinesOf(FakeOutputWindow.FakePane pane) =>
        string.Concat(pane.Lines).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

    private static string Message(string line) => line.Substring(line.IndexOf(' ') + 1).Substring(5);

    [Fact, Trait("Category", "Editor")]
    public async Task Lines_logged_from_background_threads_arrive_in_order()
    {
        var logger = new OutputWindowLogger(ThreadHelper.JoinableTaskContext);

        await Task.Run(() =>
        {
            for (var i = 1; i <= 3; i++)
            {
                logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"line {i}");
            }
        });
        await logger.DrainAsync();

        var pane = _outputWindow.Panes.Single();
        pane.Title.ShouldBe("Daml");
        LinesOf(pane).Select(l => l.Substring(l.IndexOf("info", StringComparison.Ordinal)).TrimEnd()).ShouldBe(new[] { "info line 1", "info line 2", "info line 3" });
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Each_category_gets_its_own_pane_created_on_first_use()
    {
        var logger = new OutputWindowLogger(ThreadHelper.JoinableTaskContext);

        logger.Log(DamlLogCategory.Build, DamlLogLevel.Warning, "dpm build failed");
        logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, "loaded");
        logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, "retrying");
        await logger.DrainAsync();

        _outputWindow.Panes.Select(p => p.Title).OrderBy(t => t).ShouldBe(new[] { "Daml", "Daml Build" });
        LinesOf(_outputWindow.Panes.Single(p => p.Title == "Daml Build")).Length.ShouldBe(2);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Entries_below_the_minimum_level_are_dropped_without_creating_a_pane()
    {
        var logger = new OutputWindowLogger(ThreadHelper.JoinableTaskContext) { MinimumLevel = DamlLogLevel.Warning };

        logger.IsEnabled(DamlLogLevel.Information).ShouldBeFalse();
        logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, "noise");
        await logger.DrainAsync();

        _outputWindow.Panes.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Exception_details_follow_the_message()
    {
        var logger = new OutputWindowLogger(ThreadHelper.JoinableTaskContext);

        logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "server crashed", new InvalidOperationException("exit code 3"));
        await logger.DrainAsync();

        var text = string.Concat(_outputWindow.Panes.Single().Lines);
        _outputWindow.Panes.Single().Title.ShouldBe("Daml Language Server");
        text.ShouldContain("fail server crashed");
        text.ShouldContain("System.InvalidOperationException: exit code 3");
    }

    [Fact, Trait("Category", "Editor")]
    public async Task A_burst_is_written_in_order_with_far_fewer_pane_writes_than_lines()
    {
        // Hold the first pane creation until the whole burst is queued, so only the first batch
        // depends on how fast the producer happens to be.
        var queued = new AsyncManualResetEvent();
        async Task<IVsOutputWindowPane?> CreatePaneAsync(Guid id, string title)
        {
            await queued.WaitAsync();
            return _outputWindow.Create(id, title);
        }
        var logger = new OutputWindowLogger(ThreadHelper.JoinableTaskContext, CreatePaneAsync);

        await Task.Run(() =>
        {
            for (var i = 1; i <= 1000; i++)
            {
                logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"m{i}");
            }
        });
        queued.Set();
        await logger.DrainAsync();

        var pane = _outputWindow.Panes.Single();
        LinesOf(pane).Select(Message).ShouldBe(Enumerable.Range(1, 1000).Select(i => $"m{i}"));
        // One write per pass and a bounded pass, so the UI thread gets breaks. The first pass may
        // start before the burst is fully queued, so it can be a partial batch: hence the + 1.
        pane.Lines.Count.ShouldBeLessThanOrEqualTo((1000 + OutputWindowLogger.MaxEntriesPerPass - 1) / OutputWindowLogger.MaxEntriesPerPass + 1);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Interleaved_categories_keep_their_order_within_each_pane()
    {
        var logger = new OutputWindowLogger(ThreadHelper.JoinableTaskContext);

        foreach (var (category, text) in new[]
        {
            (DamlLogCategory.Build, "b1"), (DamlLogCategory.Extension, "e1"), (DamlLogCategory.Build, "b2"),
            (DamlLogCategory.Extension, "e2"), (DamlLogCategory.Build, "b3"),
        })
        {
            logger.Log(category, DamlLogLevel.Information, text);
        }
        await logger.DrainAsync();

        LinesOf(_outputWindow.Panes.Single(p => p.Title == "Daml Build")).Select(Message).ShouldBe(new[] { "b1", "b2", "b3" });
        LinesOf(_outputWindow.Panes.Single(p => p.Title == "Daml")).Select(Message).ShouldBe(new[] { "e1", "e2" });
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Lines_logged_while_a_pane_is_still_being_created_stay_in_order()
    {
        // Pane creation goes through async VS services; a second flush must not overtake the
        // first one while it waits for the pane.
        var entered = new AsyncManualResetEvent();
        var release = new AsyncManualResetEvent();
        var calls = 0;
        async Task<IVsOutputWindowPane?> CreatePaneAsync(Guid id, string title)
        {
            if (++calls == 1)
            {
                entered.Set();
                await release.WaitAsync();
            }
            return _outputWindow.Create(id, title);
        }
        var logger = new OutputWindowLogger(ThreadHelper.JoinableTaskContext, CreatePaneAsync);

        logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, "first");
        await entered.WaitAsync();
        logger.Log(DamlLogCategory.Build, DamlLogLevel.Information, "second");
        await Task.Delay(200);
        release.Set();
        await logger.DrainAsync();

        LinesOf(_outputWindow.Panes.Single()).Select(Message).ShouldBe(new[] { "first", "second" });
    }
}
