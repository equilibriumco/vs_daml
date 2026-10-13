using Daml.VisualStudio.LanguageServer.Lenses;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Lenses;

public class ScriptLensParserTests
{
    private const string Resource = "daml://compiler?file=C%3A%2Fwork%2Fdaml%2FMain.daml&top-level-decl=setup";

    private static JObject Lens(string command = ScriptLensParser.ShowResourceCommand, JToken? arguments = null, JToken? range = null) => new()
    {
        ["command"] = new JObject { ["command"] = command, ["title"] = "Script results", ["arguments"] = arguments ?? new JArray("Script: setup", Resource) },
        ["range"] = range ?? JToken.Parse("""{"start":{"line":3,"character":0},"end":{"line":3,"character":5}}"""),
    };

    [Fact, Trait("Category", "Unit")]
    public void A_script_lens_becomes_a_lens_with_its_title_range_and_resource()
    {
        var lens = ScriptLensParser.Parse(new JArray(Lens())).ShouldHaveSingleItem();

        lens.Title.ShouldBe("Script: setup");
        lens.Range.ShouldBe(new LensRange(3, 0, 3, 5));
        lens.Resource.TopLevelDecl.ShouldBe("setup");
        lens.Resource.File.ShouldBe(@"C:\work\daml\Main.daml");
    }

    [Fact, Trait("Category", "Unit")]
    public void Lenses_that_dont_show_a_resource_or_are_malformed_are_skipped()
    {
        var answer = new JArray(
            Lens(command: "daml.somethingElse"),
            new JObject { ["range"] = Lens()["range"] },
            Lens(arguments: new JArray("Script: setup")),
            Lens(arguments: new JArray(1, Resource)),
            Lens(arguments: new JArray("Script: setup", "file:///C:/work/daml/Main.daml")),
            Lens(range: JToken.Parse("""{"start":{"line":-1,"character":0},"end":{"line":3,"character":5}}""")),
            Lens(range: JToken.Parse("""{"start":{"line":4,"character":0},"end":{"line":3,"character":5}}""")),
            Lens(range: JValue.CreateNull()),
            "not a lens",
            Lens());

        ScriptLensParser.Parse(answer).Count.ShouldBe(1);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("42")]
    public void Anything_but_a_list_of_lenses_gives_none(string json)
    {
        ScriptLensParser.Parse(JToken.Parse(json)).ShouldBeEmpty();
        ScriptLensParser.Parse(null).ShouldBeEmpty();
    }
}
