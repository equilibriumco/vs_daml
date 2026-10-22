using System;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.ScriptResults;

public class ScriptResultsUpdatesTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private readonly FakeTimeProvider _time = new();

    [Fact, Trait("Category", "Unit")]
    public async Task Only_the_last_of_a_quick_burst_is_shown()
    {
        using var updates = new ScriptResultsUpdates(Delay, _time);

        var first = updates.SubmitAsync("<p>1</p>");
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var second = updates.SubmitAsync("<p>2</p>");
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var third = updates.SubmitAsync("<p>3</p>");
        _time.Advance(Delay);

        (await first).ShouldBeNull();
        (await second).ShouldBeNull();
        (await third).ShouldBe("<p>3</p>");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Nothing_is_shown_before_the_burst_has_been_quiet_for_the_delay()
    {
        using var updates = new ScriptResultsUpdates(Delay, _time);

        var pending = updates.SubmitAsync("<p>1</p>");
        _time.Advance(Delay - TimeSpan.FromMilliseconds(1));
        pending.IsCompleted.ShouldBeFalse();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        (await pending).ShouldBe("<p>1</p>");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_same_results_again_are_not_shown_again()
    {
        using var updates = new ScriptResultsUpdates(Delay, _time);

        var first = updates.SubmitAsync("<p>same</p>");
        _time.Advance(Delay);
        (await first).ShouldBe("<p>same</p>");

        var again = updates.SubmitAsync("<p>same</p>");
        _time.Advance(Delay);
        (await again).ShouldBeNull("every show is a page reload");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Results_that_were_never_shown_are_shown_once_forgotten()
    {
        using var updates = new ScriptResultsUpdates(Delay, _time);
        var first = updates.SubmitAsync("<p>same</p>");
        _time.Advance(Delay);
        await first;

        updates.Forget();
        var again = updates.SubmitAsync("<p>same</p>");
        _time.Advance(Delay);

        (await again).ShouldBe("<p>same</p>", "after a failed load or a new page the content has to go in again");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Closing_drops_what_is_waiting_and_everything_after()
    {
        var updates = new ScriptResultsUpdates(Delay, _time);
        var pending = updates.SubmitAsync("<p>1</p>");

        updates.Dispose();

        (await pending).ShouldBeNull();
        (await updates.SubmitAsync("<p>2</p>")).ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_debouncer_says_whether_a_wait_is_still_the_latest()
    {
        using var debouncer = new Debouncer(Delay, _time);

        var first = debouncer.WaitAsync();
        var second = debouncer.WaitAsync();
        (await first).ShouldBeFalse("a newer wait replaced it, and it ended without waiting out the delay");
        second.IsCompleted.ShouldBeFalse();

        _time.Advance(Delay);
        (await second).ShouldBeTrue();
    }
}
