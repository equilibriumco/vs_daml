using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.ScriptResults;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.ScriptResults;

public class ViewStateFileTests
{
    private const string Setup = "daml://compiler?file=C%3A%2Fwork%2Fdaml%2FMain.daml&top-level-decl=setup";
    private const string Test = "daml://compiler?file=C%3A%2Fwork%2Fdaml%2FMain.daml&top-level-decl=test";

    [Fact, Trait("Category", "Unit")]
    public void Views_survive_a_round_trip()
    {
        var views = new Dictionary<string, ScriptResultsViewState>
        {
            [Setup] = new("transaction", true, false),
            [Test] = new("table", false, true),
        };

        var read = ViewStateFile.Read(ViewStateFile.Write(views));

        read.Count.ShouldBe(2);
        read[Setup].ShouldBe(views[Setup]);
        read[Test].ShouldBe(views[Test]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"version":2,"views":{}}""")]
    [InlineData("""{"version":1,"views":[]}""")]
    public void A_file_that_cant_be_read_is_no_saved_views(string text)
    {
        ViewStateFile.Read(text).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_broken_entry_is_skipped_and_the_rest_kept()
    {
        var text = $$$"""
            {"version":1,"views":{
              "{{{Setup}}}":{"selected":"transaction","showArchived":true,"showDetailedDisclosure":false},
              "{{{Test}}}":{"selected":"graph","showArchived":true,"showDetailedDisclosure":false},
              "other":{"selected":"table","showArchived":"yes","showDetailedDisclosure":false},
              "short":{"selected":"table"}
            }}
            """;

        var read = ViewStateFile.Read(text);

        read.Keys.ShouldBe([Setup]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Only_the_most_recent_views_are_kept_once_there_are_too_many()
    {
        var views = Enumerable.Range(0, ViewStateFile.MaxViews + 10)
            .Select(i => new KeyValuePair<string, ScriptResultsViewState>($"daml://compiler?top-level-decl=s{i}", ScriptResultsViewState.Default));

        var read = ViewStateFile.Read(ViewStateFile.Write(views));

        read.Count.ShouldBe(ViewStateFile.MaxViews);
        read.ContainsKey("daml://compiler?top-level-decl=s0").ShouldBeFalse("the oldest go first");
        read.ContainsKey($"daml://compiler?top-level-decl=s{ViewStateFile.MaxViews + 9}").ShouldBeTrue();
    }
}
