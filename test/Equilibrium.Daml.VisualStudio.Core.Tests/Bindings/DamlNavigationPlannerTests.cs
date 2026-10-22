using System.Linq;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Bindings;

public class DamlNavigationPlannerTests
{
    private const string Package = "bd2aa85e017dad908dbd9c8103929df3edaa7b0961b675d3ef216644565f9cb1";

    private static readonly DamlSymbolId Asset = new(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template);
    private static readonly DamlSymbolId Give = new(Package, "Kinds.Asset", "Asset", "Give", DamlSymbolKind.Choice);

    private static SourceCandidate Source(string name, SourceStatus status) => new($@"C:\work\{name}\daml.yaml", status, []);

    [Fact, Trait("Category", "Unit")]
    public void One_current_source_is_gone_to_and_stale_ones_beside_it_dont_matter()
    {
        var plan = DamlNavigationPlanner.Plan([(Asset, [Source("Contracts", SourceStatus.Current), Source("Old", SourceStatus.Stale)])]);

        plan.Kind.ShouldBe(DamlNavigationKind.Go);
        plan.Targets.ShouldHaveSingleItem().Source.ManifestPath.ShouldBe(@"C:\work\Contracts\daml.yaml");
    }

    // Never the first one found: whoever navigates picks.
    [Fact, Trait("Category", "Unit")]
    public void Several_current_sources_are_offered_to_choose_from()
    {
        var plan = DamlNavigationPlanner.Plan([(Asset, [Source("Contracts", SourceStatus.Current), Source("Copy", SourceStatus.Current)])]);

        plan.Kind.ShouldBe(DamlNavigationKind.Choose);
        plan.Targets.Select(t => t.Source.ManifestPath).ShouldBe([@"C:\work\Contracts\daml.yaml", @"C:\work\Copy\daml.yaml"]);
        plan.Message.ShouldContain("Kinds.Asset:Asset");
    }

    [Fact, Trait("Category", "Unit")]
    public void Two_symbols_each_with_a_source_are_a_choice_too()
    {
        var plan = DamlNavigationPlanner.Plan([(Asset, [Source("Contracts", SourceStatus.Current)]), (Give, [Source("Contracts", SourceStatus.Current)])]);

        plan.Kind.ShouldBe(DamlNavigationKind.Choose);
        plan.Targets.Select(t => t.Symbol).ShouldBe([Asset, Give]);
    }

    // Edited without building: the C# no longer says where the code is.
    [Fact, Trait("Category", "Unit")]
    public void Only_stale_sources_say_so_and_ask_for_a_build()
    {
        var plan = DamlNavigationPlanner.Plan([(Give, [Source("Contracts", SourceStatus.Stale)])]);

        plan.Kind.ShouldBe(DamlNavigationKind.Stale);
        plan.Targets.ShouldHaveSingleItem();
        plan.Message.ShouldContain("Kinds.Asset:Asset.Give");
        plan.Message.ShouldContain("Build");
    }

    [Fact, Trait("Category", "Unit")]
    public void No_local_source_is_external()
    {
        var plan = DamlNavigationPlanner.Plan([(Asset, [])]);

        plan.Kind.ShouldBe(DamlNavigationKind.External);
        plan.Targets.ShouldBeEmpty();
        plan.Message.ShouldContain(Package);
    }
}
