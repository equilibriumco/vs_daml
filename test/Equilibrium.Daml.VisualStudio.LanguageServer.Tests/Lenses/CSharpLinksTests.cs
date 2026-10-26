using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.LanguageServer.Lenses;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Lenses;

// Whether a Daml package's C# references can be counted: only from generations whose recorded
// sources are the Daml on disk now, and a stale one says so instead of counting old code.
public class CSharpLinksTests
{
    [Fact, Trait("Category", "Unit")]
    public void Generations_from_the_current_sources_link_each_package_once()
    {
        var (state, packages) = CSharpLinks.From([("pkg-a", SourceStatus.Current), ("pkg-b", SourceStatus.Stale), ("pkg-a", SourceStatus.Current), ("pkg-c", SourceStatus.Current)]);

        state.ShouldBe(CSharpLinkState.Current);
        packages.ShouldBe(["pkg-a", "pkg-c"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Only_stale_generations_make_a_stale_link_without_packages()
    {
        var (state, packages) = CSharpLinks.From([("pkg-a", SourceStatus.Stale), ("pkg-b", SourceStatus.External)]);

        state.ShouldBe(CSharpLinkState.Stale);
        packages.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void No_generation_from_these_sources_is_no_link()
    {
        CSharpLinks.From([("pkg-a", SourceStatus.External)]).State.ShouldBe(CSharpLinkState.None);
        CSharpLinks.From([]).State.ShouldBe(CSharpLinkState.None);
    }
}
