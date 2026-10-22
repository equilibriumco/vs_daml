using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.ScriptResults;

public class ResultsNavigationTests
{
    private const string Reveal = "command:daml.revealLocation?%5B%22file%3A%2F%2FC%3A%2Fwork%2Fdaml%2FMain.daml%22%2C%2033%2C%2037%5D";

    [Theory, Trait("Category", "Unit")]
    [InlineData("https://daml-webview.invalid/results.html")]
    [InlineData("https://daml-webview.invalid/results.html?v=7")]
    [InlineData("https://daml-webview.invalid/results.html?v=7#n1")]
    [InlineData("HTTPS://DAML-WEBVIEW.INVALID/results.html?v=2")]
    public void The_results_page_itself_may_load(string uri)
    {
        ResultsNavigation.Classify(uri, userInitiated: false).Kind.ShouldBe(NavigationKind.OwnPage);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("https://daml-webview.invalid/webview.js")]
    [InlineData("https://daml-webview.invalid/other.html")]
    [InlineData("https://daml-webview.invalid:8443/results.html")]
    [InlineData("https://user@daml-webview.invalid/results.html")]
    [InlineData("http://daml-webview.invalid/results.html")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("data:text/html,<p>x</p>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("command:workbench.action.quit")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_that_isnt_a_link_the_page_has_is_blocked(string? uri)
    {
        var decision = ResultsNavigation.Classify(uri, userInitiated: true);

        decision.Kind.ShouldBe(NavigationKind.Blocked);
        decision.Rejection.ShouldNotBeNullOrEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_clicked_source_link_reveals_its_location()
    {
        var decision = ResultsNavigation.Classify(Reveal, userInitiated: true);

        decision.Kind.ShouldBe(NavigationKind.RevealLocation);
        decision.Reveal.ShouldBe(new RevealRequest(@"C:\work\daml\Main.daml", 33, 37));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_source_link_the_user_didnt_click_is_blocked()
    {
        ResultsNavigation.Classify(Reveal, userInitiated: false).Kind.ShouldBe(NavigationKind.Blocked);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_broken_source_link_is_blocked_and_says_why()
    {
        var decision = ResultsNavigation.Classify("command:daml.revealLocation?%5B%22file%3A%2F%2FC%3A%2Fwork%2FMain.cs%22%2C1%2C2%5D", userInitiated: true);

        decision.Kind.ShouldBe(NavigationKind.Blocked);
        decision.Rejection.ShouldNotBeNull().ShouldContain("Daml");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("https://docs.digitalasset.com/build/3.4/")]
    [InlineData("http://example.com/x?y=1")]
    public void A_clicked_web_link_goes_to_the_browser(string uri)
    {
        var decision = ResultsNavigation.Classify(uri, userInitiated: true);

        decision.Kind.ShouldBe(NavigationKind.External);
        decision.External!.AbsoluteUri.ShouldStartWith(uri.Substring(0, 10));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_web_address_the_page_goes_to_by_itself_is_blocked()
    {
        ResultsNavigation.Classify("https://example.com/", userInitiated: false).Kind.ShouldBe(NavigationKind.Blocked);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_web_link_with_credentials_is_blocked()
    {
        ResultsNavigation.Classify("https://user:secret@example.com/", userInitiated: true).Kind.ShouldBe(NavigationKind.Blocked);
    }
}
