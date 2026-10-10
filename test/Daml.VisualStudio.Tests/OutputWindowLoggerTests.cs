using System;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Logging;
using Microsoft.VisualStudio.Sdk.TestFramework;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
        pane.Lines.Select(l => l.Substring(l.IndexOf("info", StringComparison.Ordinal)).TrimEnd()).ShouldBe(new[] { "info line 1", "info line 2", "info line 3" });
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
        _outputWindow.Panes.Single(p => p.Title == "Daml Build").Lines.Count.ShouldBe(2);
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
}
