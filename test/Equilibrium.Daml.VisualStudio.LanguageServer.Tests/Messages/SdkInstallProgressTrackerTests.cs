using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Messages;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Messages;

public class SdkInstallProgressTrackerTests
{
    private readonly FakePresenter _presenter = new();
    private readonly List<string> _cancelled = new();

    private SdkInstallProgressTracker Tracker() => new(_presenter, id =>
    {
        _cancelled.Add(id);
        return Task.FromResult(true);
    }, NullDamlLogger.Instance);

    private static SdkInstallProgressParams Step(SdkInstallProgressKind kind, int progress, string id = "3.6.0") =>
        new(id, "SDK " + id, kind, progress);

    [Fact, Trait("Category", "Unit")]
    public void An_install_is_shown_from_begin_through_its_reports_to_the_end()
    {
        var tracker = Tracker();

        tracker.Report(Step(SdkInstallProgressKind.Begin, 0));
        tracker.Report(Step(SdkInstallProgressKind.Report, 30));
        tracker.Report(Step(SdkInstallProgressKind.Report, 75));
        tracker.Report(Step(SdkInstallProgressKind.End, 100));

        var view = _presenter.Views.ShouldHaveSingleItem();
        view.Title.ShouldBe("Installing Daml SDK 3.6.0");
        view.Percentages.ShouldBe([0, 30, 75]);
        view.Completed.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_report_without_a_begin_starts_showing_the_install()
    {
        var tracker = Tracker();

        tracker.Report(Step(SdkInstallProgressKind.Report, 10));

        _presenter.Views.ShouldHaveSingleItem().Percentages.ShouldBe([10]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Installs_of_different_sdks_are_shown_separately_and_progress_stays_within_0_to_100()
    {
        var tracker = Tracker();

        tracker.Report(Step(SdkInstallProgressKind.Begin, -5, "3.5.2"));
        tracker.Report(Step(SdkInstallProgressKind.Begin, 0, "3.6.0"));
        tracker.Report(Step(SdkInstallProgressKind.Report, 250, "3.5.2"));

        _presenter.Views.Count.ShouldBe(2);
        _presenter.Views[0].Percentages.ShouldBe([0, 100]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Cancelling_in_the_ui_asks_the_server_to_cancel_that_install()
    {
        var tracker = Tracker();
        tracker.Report(Step(SdkInstallProgressKind.Begin, 0));

        await _presenter.Views[0].CancelAsync();

        _cancelled.ShouldBe(["3.6.0"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_end_for_an_unknown_install_is_ignored()
    {
        Tracker().Report(Step(SdkInstallProgressKind.End, 100));

        _presenter.Views.ShouldBeEmpty();
    }

    private sealed class FakePresenter : ISdkInstallPresenter
    {
        public List<FakeView> Views { get; } = new();

        public ISdkInstallView Show(string title, Func<Task> cancel)
        {
            var view = new FakeView(title, cancel);
            Views.Add(view);
            return view;
        }
    }

    private sealed class FakeView(string title, Func<Task> cancel) : ISdkInstallView
    {
        public string Title { get; } = title;

        public List<int> Percentages { get; } = new();

        public bool Completed { get; private set; }

        public Task CancelAsync() => cancel();

        public void Report(int percentComplete) => Percentages.Add(percentComplete);

        public void Complete() => Completed = true;
    }
}
