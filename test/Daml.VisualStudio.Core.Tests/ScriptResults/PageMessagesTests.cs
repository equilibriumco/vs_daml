using Daml.VisualStudio.Core.ScriptResults;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.ScriptResults;

public class PageMessagesTests
{
    private const string Page = "https://daml-webview.invalid/results.html";

    private static ScriptResultsViewState Apply(string json, ScriptResultsViewState? state = null)
    {
        var message = PageMessages.Parse(Page, json);
        message.Rejection.ShouldBeNull();
        return (state ?? ScriptResultsViewState.Default).Apply(message.Command!, message.Value!);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_page_starts_on_the_table_with_archived_contracts_and_disclosure_hidden() =>
        ScriptResultsViewState.Default.ShouldBe(new ScriptResultsViewState("table", ShowArchived: false, ShowDetailedDisclosure: false));

    [Fact, Trait("Category", "Unit")]
    public void Each_page_command_changes_its_part_of_the_view()
    {
        var state = Apply("""{"command":"set_selected_view","value":"transaction"}""");
        state = Apply("""{"command":"set_show_archived","value":true}""", state);
        state = Apply("""{"command":"set_show_detailed_disclosure","value":true}""", state);

        state.ShouldBe(new ScriptResultsViewState("transaction", true, true));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("\"set_selected_view\"", "a plain string")]
    [InlineData("""{"command":"workbench.action.quit"}""", "an unknown command")]
    [InlineData("""{"command":"set_selected_view","value":"graph"}""", "a view the page doesn't have")]
    [InlineData("""{"command":"set_show_archived","value":"yes"}""", "a string where a flag belongs")]
    [InlineData("""{"command":"set_show_archived","value":true,"extra":1}""", "a field too many")]
    [InlineData("""{"value":true}""", "no command")]
    [InlineData("""{"command":7,"value":true}""", "a command that isn't text")]
    [InlineData("{not json", "something that isn't JSON")]
    public void Anything_but_a_known_command_with_the_right_value_is_refused(string json, string what)
    {
        PageMessages.Parse(Page, json).Rejection.ShouldNotBeNull(what);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_message_from_another_origin_is_refused()
    {
        PageMessages.Parse("https://example.com/", """{"command":"set_show_archived","value":true}""").Rejection.ShouldNotBeNull();
        PageMessages.Parse("https://daml-webview.invalid.example.com/", """{"command":"set_show_archived","value":true}""").Rejection.ShouldNotBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void An_oversized_message_is_refused_without_being_parsed()
    {
        var json = "{\"command\":\"set_selected_view\",\"value\":\"table\",\"pad\":\"" + new string('x', 5000) + "\"}";

        PageMessages.Parse(Page, json).Rejection.ShouldNotBeNull().ShouldContain("chars");
    }

    [Fact, Trait("Category", "Unit")]
    public void The_view_goes_back_to_the_page_in_its_own_shape()
    {
        var json = JObject.Parse(PageMessages.SetView(new ScriptResultsViewState("transaction", true, false)));

        JToken.DeepEquals(json, JObject.Parse("""{"command":"set_view","value":{"selected":"transaction","showArchived":true,"showDetailedDisclosure":false}}""")).ShouldBeTrue(json.ToString());
    }

    [Fact, Trait("Category", "Unit")]
    public void A_note_reaches_the_page_as_text_because_the_page_puts_it_into_innerHTML()
    {
        var json = JObject.Parse(PageMessages.AddNote("<img src=x onerror=alert(1)> & done"));

        ((string)json["command"]!).ShouldBe("add_note");
        ((string)json["value"]!).ShouldBe("&lt;img src=x onerror=alert(1)&gt; &amp; done");
    }
}
