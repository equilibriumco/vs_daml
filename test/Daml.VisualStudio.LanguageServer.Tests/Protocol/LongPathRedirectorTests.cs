using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Protocol;

public class LongPathRedirectorTests
{
    private const string Root = @"C:\Users\me\AppData\Local\Daml\VisualStudio\library";

    // Like the paths damlc gives for library sources: well over the 260 characters Visual Studio opens.
    private static readonly string LongPath = @"C:\work\pkg\.daml\package-database\2.3\daml-script-stable-Daml-Script-Internal-Questions-PartyManagement-Stable-PartyIdHint-1.0.0-"
        + new string('a', 64) + @"\Daml\Script\Internal\Questions\PartyManagement\Stable\PartyIdHint.daml";

    private const string ShortPath = @"C:\work\pkg\daml\Main.daml";

    private readonly FakeCopier _copier = new();

    private LongPathRedirector Redirector() => new(Root, _copier);

    private static string Uri(string path) => new Uri(path).AbsoluteUri;

    private static JObject Location(string path) => JObject.FromObject(new { uri = Uri(path), range = new { start = new { line = 5, character = 2 }, end = new { line = 5, character = 13 } } });

    [Fact, Trait("Category", "Unit")]
    public void A_long_target_is_replaced_by_its_short_copy_and_the_range_is_kept()
    {
        LongPath.Length.ShouldBeGreaterThan(260);

        var result = (JObject)Redirector().Redirect(Location(LongPath))!;

        var copied = _copier.Copies.ShouldHaveSingleItem();
        copied.Source.ShouldBe(LongPath);
        copied.Target.ShouldStartWith(Root + @"\");
        Path.GetFileName(copied.Target).ShouldBe("PartyIdHint.daml");
        copied.Target.Length.ShouldBeLessThan(200);
        ((string)result["uri"]!).ShouldBe(Uri(copied.Target));
        JToken.DeepEquals(result["range"], Location(LongPath)["range"]).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public void Short_targets_and_other_schemes_are_left_alone()
    {
        var input = new JArray(Location(ShortPath), JObject.FromObject(new { uri = "daml://compiler?file=x", range = new { } }));

        var result = Redirector().Redirect(input);

        result.ShouldBeSameAs(input);
        _copier.Copies.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Arrays_and_location_links_are_handled()
    {
        var link = JObject.FromObject(new { targetUri = Uri(LongPath), targetRange = new { }, targetSelectionRange = new { } });

        var result = (JArray)Redirector().Redirect(new JArray(Location(ShortPath), Location(LongPath), link))!;

        ((string)result[0]["uri"]!).ShouldBe(Uri(ShortPath));
        ((string)result[1]["uri"]!).ShouldNotBe(Uri(LongPath));
        ((string)result[2]["targetUri"]!).ShouldBe((string)result[1]["uri"]!, "the same source maps to the same copy");
    }

    [Fact, Trait("Category", "Unit")]
    public void Different_sources_with_the_same_name_get_different_copies()
    {
        var other = LongPath.Replace("2.3", "2.2");

        Redirector().Redirect(new JArray(Location(LongPath), Location(other)));

        _copier.Copies.Select(c => c.Target).Distinct().Count().ShouldBe(2);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_copy_that_fails_leaves_the_original_target()
    {
        _copier.Fail = true;
        var input = Location(LongPath);

        var result = Redirector().Redirect(input);

        result.ShouldBeSameAs(input);
    }

    [Fact, Trait("Category", "Unit")]
    public void Null_and_empty_results_pass_through()
    {
        Redirector().Redirect(null).ShouldBeNull();
        var empty = new JArray();
        Redirector().Redirect(empty).ShouldBeSameAs(empty);
    }

    private sealed class FakeCopier : ILibraryCopier
    {
        public List<(string Source, string Target)> Copies { get; } = new();

        public bool Fail { get; set; }

        public bool CopyReadOnly(string source, string target)
        {
            Copies.Add((source, target));
            return !Fail;
        }
    }
}
