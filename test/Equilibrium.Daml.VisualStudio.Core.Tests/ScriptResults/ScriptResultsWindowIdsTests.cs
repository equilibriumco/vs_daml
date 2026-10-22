using System;
using System.Collections.Generic;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.ScriptResults;

public class ScriptResultsWindowIdsTests
{
    private const string Setup = "daml://compiler?file=C%3A%2Fwork%2Fdaml%2FMain.daml&top-level-decl=setup";
    private const string Test = "daml://compiler?file=C%3A%2Fwork%2Fdaml%2FMain.daml&top-level-decl=test";

    [Fact, Trait("Category", "Unit")]
    public void The_same_resource_always_gets_the_same_window()
    {
        var ids = new ScriptResultsWindowIds();

        var first = ids.GetOrAdd(Setup);
        ids.GetOrAdd(Setup).ShouldBe(first);
        ids.GetOrAdd(Test).ShouldNotBe(first);
        ids.TryGetResource(first, out var resource).ShouldBeTrue();
        resource.ShouldBe(Setup);
    }

    [Fact, Trait("Category", "Unit")]
    public void Resources_whose_hashes_collide_still_get_their_own_windows()
    {
        var ids = new ScriptResultsWindowIds(new SameHash());

        var setup = ids.GetOrAdd(Setup);
        var test = ids.GetOrAdd(Test);

        test.ShouldNotBe(setup);
        ids.GetOrAdd(Setup).ShouldBe(setup);
        ids.GetOrAdd(Test).ShouldBe(test);
        ids.TryGetResource(test, out var resource).ShouldBeTrue();
        resource.ShouldBe(Test);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_closed_window_frees_its_id_for_nothing_but_its_own_resource_lookup()
    {
        var ids = new ScriptResultsWindowIds(new SameHash());
        var setup = ids.GetOrAdd(Setup);
        var test = ids.GetOrAdd(Test);

        ids.Remove(setup);

        ids.TryGetResource(setup, out _).ShouldBeFalse();
        ids.GetOrAdd(Test).ShouldBe(test, "a free slot before it must not hide an entry that probed past it");
    }

    [Fact, Trait("Category", "Unit")]
    public void Ids_are_never_negative()
    {
        var ids = new ScriptResultsWindowIds(new FixedHash(int.MinValue));

        ids.GetOrAdd(Setup).ShouldBeGreaterThanOrEqualTo(0);
        ids.GetOrAdd(Test).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_comparer_decides_which_resources_are_the_same()
    {
        var ids = new ScriptResultsWindowIds(StringComparer.OrdinalIgnoreCase);

        ids.GetOrAdd(Setup).ShouldBe(ids.GetOrAdd(Setup.ToUpperInvariant()));
    }

    private sealed class SameHash : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        public int GetHashCode(string obj) => 42;
    }

    private sealed class FixedHash(int hash) : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        public int GetHashCode(string obj) => hash;
    }
}
