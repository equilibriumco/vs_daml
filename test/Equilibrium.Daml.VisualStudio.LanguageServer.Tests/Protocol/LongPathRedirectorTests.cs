using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Protocol;

public class LongPathRedirectorTests
{
    private const string Root = @"C:\Users\me\AppData\Local\DamlVisualStudio\library";

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

    // Visual Studio escapes the drive colon and damlc echoes it back; Uri alone would turn that
    // into "/c:/...", which is no Windows path.
    [Fact, Trait("Category", "Unit")]
    public void A_long_target_with_an_escaped_drive_colon_is_redirected_too()
    {
        var escaped = Uri(LongPath).Replace("file:///C:/", "file:///c%3A/");

        var result = (JObject)Redirector().Redirect(JObject.FromObject(new { uri = escaped, range = new { } }))!;

        _copier.Copies.ShouldHaveSingleItem().Source.ShouldBe(LongPath, StringCompareShould.IgnoreCase);
        ((string)result["uri"]!).ShouldStartWith("file:///C:/");
    }

    // The limit is the one Visual Studio applies: 260 characters including the terminator, so a
    // 259-character path still opens and a 260-character one doesn't.
    [Theory, Trait("Category", "Unit")]
    [InlineData(259, false)]
    [InlineData(260, true)]
    [InlineData(261, true)]
    public void Paths_are_redirected_from_exactly_260_characters(int length, bool redirected)
    {
        var folder = @"C:\p\";
        var path = folder + new string('a', length - folder.Length - @"\F.daml".Length) + @"\F.daml";
        path.Length.ShouldBe(length);

        var result = Redirector().Redirect(Location(path));

        (_copier.Copies.Count > 0).ShouldBe(redirected);
        ((string)result!["uri"]!).Equals(Uri(path), StringComparison.Ordinal).ShouldBe(!redirected);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_space_in_the_long_path_survives_the_uri_escaping()
    {
        var path = LongPath.Replace(@"\Daml\Script\", @"\Daml Script\");

        Redirector().Redirect(Location(path));

        _copier.Copies.ShouldHaveSingleItem().Source.ShouldBe(path);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_location_link_keeps_its_ranges()
    {
        var link = JObject.FromObject(new { targetUri = Uri(LongPath), targetRange = new { start = new { line = 1, character = 2 }, end = new { line = 3, character = 4 } }, targetSelectionRange = new { start = new { line = 1, character = 2 }, end = new { line = 1, character = 9 } } });

        var result = (JObject)Redirector().Redirect(link)!;

        JToken.DeepEquals(result["targetRange"], link["targetRange"]).ShouldBeTrue();
        JToken.DeepEquals(result["targetSelectionRange"], link["targetSelectionRange"]).ShouldBeTrue();
        ((string)result["targetUri"]!).ShouldNotBe(Uri(LongPath));
    }

    [Fact, Trait("Category", "Unit")]
    public void Two_long_files_of_the_same_folder_get_their_own_copies()
    {
        var sibling = LongPath.Replace("PartyIdHint.daml", "PartyIdHints.daml");

        Redirector().Redirect(new JArray(Location(LongPath), Location(sibling)));

        _copier.Copies.Select(c => c.Target).Distinct().Count().ShouldBe(2);
        _copier.Copies.Select(c => Path.GetFileName(c.Target)).ShouldBe(["PartyIdHint.daml", "PartyIdHints.daml"]);
    }
    [Fact, Trait("Category", "Unit")]
    public void A_library_copy_document_maps_back_to_its_source_without_mutating_the_request()
    {
        var redirector = Redirector();
        var copy = (JObject)redirector.Redirect(Location(LongPath))!;
        var parameters = JObject.FromObject(new { textDocument = new { uri = (string)copy["uri"]! }, position = new { line = 5, character = 2 } });
        var result = redirector.OriginalDocument(parameters);
        ((string)result["textDocument"]!["uri"]!).ShouldBe(Uri(LongPath));
        JToken.DeepEquals(result["position"], parameters["position"]).ShouldBeTrue();
        ((string)parameters["textDocument"]!["uri"]!).ShouldBe((string)copy["uri"]!);
    }

    [Fact, Trait("Category", "Unit")]
    public void Long_unc_paths_use_the_unc_extended_prefix()
    {
        var path = @"\\server\share\" + new string('a', 250) + @"\Library.daml";
        LibraryCopier.Extended(path).ShouldBe(@"\\?\UNC\server\share\" + new string('a', 250) + @"\Library.daml");
    }

}
