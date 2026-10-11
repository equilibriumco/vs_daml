using System;
using System.Linq;
using Daml.VisualStudio.LanguageServer.Supervision;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Supervision;

public class RestartPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact, Trait("Category", "Unit")]
    public void Consecutive_crashes_back_off_one_two_four_eight_sixteen_seconds()
    {
        var policy = new RestartPolicy();

        var delays = Enumerable.Range(0, 5).Select(i => policy.RecordCrash(T0.AddSeconds(i * 10))).ToList();

        delays.ShouldAllBe(d => !d.OpenCircuit);
        delays.Select(d => d.Delay.TotalSeconds).ShouldBe([1, 2, 4, 8, 16]);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_sixth_crash_within_three_minutes_opens_the_circuit()
    {
        var policy = new RestartPolicy();
        for (var i = 0; i < 5; i++)
        {
            policy.RecordCrash(T0.AddSeconds(i * 30));
        }

        policy.RecordCrash(T0.AddSeconds(170)).OpenCircuit.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public void Crashes_older_than_the_window_no_longer_count()
    {
        var policy = new RestartPolicy();
        for (var i = 0; i < 5; i++)
        {
            policy.RecordCrash(T0.AddSeconds(i));
        }

        var later = policy.RecordCrash(T0.AddMinutes(3).AddSeconds(5));

        later.OpenCircuit.ShouldBeFalse();
        later.Delay.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact, Trait("Category", "Unit")]
    public void Reset_forgets_every_crash()
    {
        var policy = new RestartPolicy();
        for (var i = 0; i < 6; i++)
        {
            policy.RecordCrash(T0.AddSeconds(i));
        }

        policy.Reset();

        policy.RecordCrash(T0.AddSeconds(10)).ShouldBe(new RestartDecision(false, TimeSpan.FromSeconds(1)));
    }
}
