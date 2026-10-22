using System.IO;
using System.Linq;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.ScriptResults;

// What the results page may load, which sources its links may open, which page messages still
// count after a reload, whether the server's HTML still fits the shipped script, and which
// browser runtimes are good enough.
public class ResultsBoundaryTests
{
    private const string Page = "https://daml-webview.invalid/results.html?v=3";
    private const string Archived = """{"command":"set_show_archived","value":true}""";

    private static string Fixture(string sdk) =>
        File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "script-results", $"sdk-{sdk}-setup.html"));

    [Theory, Trait("Category", "Unit")]
    [InlineData("https://daml-webview.invalid/results.html?v=3", true)]
    [InlineData("https://daml-webview.invalid/webview.js", true)]
    [InlineData("https://daml-webview.invalid/WEBVIEW.CSS", true)]
    [InlineData("https://daml-webview.invalid/shim.js", true)]
    [InlineData("https://daml-webview.invalid/theme.css?x", true)]
    [InlineData("https://daml-webview.invalid/../secret.txt", false)]
    [InlineData("https://daml-webview.invalid/%2e%2e/secret.txt", false)]
    [InlineData("https://daml-webview.invalid/sub/webview.js", false)]
    [InlineData("https://daml-webview.invalid/LICENSE", false)]
    [InlineData("https://daml-webview.invalid:444/webview.js", false)]
    [InlineData("http://daml-webview.invalid/webview.js", false)]
    [InlineData("https://example.com/webview.js", false)]
    [InlineData("https://fonts.googleapis.com/css?family=x", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("", false)]
    public void The_page_may_load_only_its_own_files(string uri, bool allowed)
    {
        ResultsResources.IsAllowed(uri).ShouldBe(allowed);
    }

    [Fact, Trait("Category", "Unit")]
    public void Links_open_only_files_under_the_given_roots()
    {
        var sources = new AllowedSources([@"C:\work\app", @"C:\Users\me\AppData\Local\DamlVisualStudio\library"]);

        sources.Contains(@"C:\work\app\daml\Main.daml").ShouldBeTrue();
        sources.Contains(@"c:\WORK\APP\daml\Main.daml").ShouldBeTrue();
        sources.Contains(@"C:\Users\me\AppData\Local\DamlVisualStudio\library\ab12\DA\List.daml").ShouldBeTrue();
        sources.Contains(@"C:\work\app-other\daml\Main.daml").ShouldBeFalse();
        sources.Contains(@"C:\work\Main.daml").ShouldBeFalse();
        sources.Contains(@"D:\work\app\daml\Main.daml").ShouldBeFalse();
        sources.Contains("relative\\Main.daml").ShouldBeFalse();
        sources.Contains("").ShouldBeFalse();
        new AllowedSources([]).Contains(@"C:\work\app\daml\Main.daml").ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_message_from_the_page_now_shown_counts()
    {
        PageMessages.Parse(Page, Archived, currentPage: Page).Rejection.ShouldBeNull();
        PageMessages.Parse(Page + "#n1", Archived, currentPage: Page).Rejection.ShouldBeNull("following an anchor on the page is still the same page");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_message_from_a_page_that_has_since_been_replaced_is_dropped()
    {
        PageMessages.Parse("https://daml-webview.invalid/results.html?v=2", Archived, currentPage: Page).Rejection.ShouldNotBeNull().ShouldContain("earlier");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("https://daml-webview.invalid/shim.js")]
    [InlineData("https://daml-webview.invalid/other.html")]
    [InlineData("about:blank")]
    public void Only_the_results_page_may_send_messages(string source)
    {
        PageMessages.Parse(source, Archived).Rejection.ShouldNotBeNull();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("3.5.2")]
    [InlineData("3.6.0")]
    public void The_results_of_both_supported_sdks_fit_the_shipped_page_script(string sdk)
    {
        ResultsPageFormat.MissingParts(Fixture(sdk)).ShouldBeEmpty();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("toggle_view();", "toggle_view()")]
    [InlineData("id=\"show_archived\"", "show_archived")]
    [InlineData("id=\"note\"", "note")]
    [InlineData("$webviewSrc", "$webviewSrc")]
    [InlineData("hide_transaction", "hide_transaction")]
    public void Results_in_a_format_the_page_script_doesnt_know_are_named(string removed, string named)
    {
        var html = Fixture("3.6.0").Replace(removed, "something_else");

        ResultsPageFormat.MissingParts(html).ShouldContain(part => part.Contains(named));
    }

    [Fact, Trait("Category", "Unit")]
    public void The_page_for_an_unknown_format_says_what_to_do_and_names_nothing_from_the_server()
    {
        var page = ScriptResultsPage.Unsupported(["toggle_view()"]);

        page.ShouldContain("toggle_view()");
        page.ShouldContain("Update");
        page.ShouldNotContain("$webviewSrc");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("154.0.4258.62", RuntimeStatus.Ready)]
    [InlineData("120.0.2210.91", RuntimeStatus.Ready)]
    [InlineData("119.0.2151.97", RuntimeStatus.TooOld)]
    [InlineData("86.0.616.0", RuntimeStatus.TooOld)]
    [InlineData("154.0.4258.62 beta", RuntimeStatus.Ready)]
    [InlineData(null, RuntimeStatus.Missing)]
    [InlineData("", RuntimeStatus.Missing)]
    [InlineData("not a version", RuntimeStatus.Missing)]
    public void Browser_runtimes_older_than_the_minimum_are_refused(string? version, RuntimeStatus status)
    {
        var check = WebViewRuntime.Check(version);

        check.Status.ShouldBe(status);
        if (status != RuntimeStatus.Ready)
        {
            check.Message.ShouldContain("WebView2");
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void The_messages_for_a_broken_runtime_say_how_to_fix_it()
    {
        WebViewRuntime.Check(null).Message.ShouldContain("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        WebViewRuntime.Check("100.0.1.1").Message.ShouldContain("100.0.1.1");
        WebViewRuntime.TimedOut(System.TimeSpan.FromSeconds(30)).ShouldContain("30");
        WebViewRuntime.Failed("No such interface supported").ShouldContain("No such interface supported");
        WebViewRuntime.Failed(new string('x', 5000)).Length.ShouldBeLessThan(400);
        WebViewRuntime.Failed(null).ShouldContain(WebViewRuntime.DownloadLink);
        new[] { WebViewRuntime.Check(null).Message, WebViewRuntime.Check("100.0.1.1").Message }.ShouldAllBe(m => m.Length < 400);
    }
}
