using System.IO;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.ScriptResults;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.ScriptResults;

public class ScriptResultsPageTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "script-results", "sdk-3.5.2-setup.html"));

    [Fact, Trait("Category", "Unit")]
    public void The_placeholders_point_at_the_shipped_script_and_stylesheet()
    {
        var page = ScriptResultsPage.Compose(Fixture());

        page.ShouldNotContain("$webviewSrc");
        page.ShouldNotContain("$webviewCss");
        page.ShouldContain("<script src=\"https://daml-webview.invalid/webview.js\"></script>");
        page.ShouldContain("<link rel=\"stylesheet\" href=\"https://daml-webview.invalid/webview.css\">");
    }

    [Fact, Trait("Category", "Unit")]
    public void The_policy_shim_and_theme_come_first_in_the_head()
    {
        var page = ScriptResultsPage.Compose("<!DOCTYPE HTML><html><HEAD><script src=\"$webviewSrc\"></script></HEAD><body></body></html>");

        var head = page.IndexOf("<HEAD>", System.StringComparison.Ordinal) + "<HEAD>".Length;
        page.IndexOf("Content-Security-Policy", System.StringComparison.Ordinal).ShouldBeGreaterThan(head);
        page.IndexOf("shim.js", System.StringComparison.Ordinal).ShouldBeLessThan(page.IndexOf("webview.js", System.StringComparison.Ordinal),
            "webview.js calls acquireVsCodeApi as soon as it loads, so the shim must already be there");
        page.ShouldContain("<link rel=\"stylesheet\" href=\"https://daml-webview.invalid/theme.css\">");
    }

    [Fact, Trait("Category", "Unit")]
    public void The_policy_allows_nothing_from_outside_the_origin()
    {
        var page = ScriptResultsPage.Compose(Fixture());

        page.ShouldContain("default-src 'none'");
        page.ShouldContain("connect-src 'none'");
        page.ShouldContain("frame-src 'none'");
        page.ShouldContain("form-action 'none'");
        page.ShouldContain("base-uri 'none'");
        page.ShouldContain("script-src https://daml-webview.invalid/ 'unsafe-inline'");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_page_without_a_head_still_gets_the_policy_before_anything_else()
    {
        var page = ScriptResultsPage.Compose("<p>hi</p>");

        page.ShouldStartWith("<meta http-equiv=\"Content-Security-Policy\"");
        page.ShouldEndWith("<p>hi</p>");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_message_page_shows_its_text_as_text()
    {
        var page = ScriptResultsPage.Message("Results for <Main:setup> & more");

        page.ShouldContain("Results for &lt;Main:setup&gt; &amp; more");
        page.ShouldNotContain("<Main:setup>");
        page.ShouldContain("Content-Security-Policy");
        page.ShouldContain("theme.css");
    }
}
