using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Daml.TestHelpers;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

// Comment toggling, auto-closing and word selection come from a language configuration file
// that Visual Studio picks up by content type. Nothing in the editor tells us when the file is
// wrong or missing, so its contents and registration are pinned here.
public class LanguageConfigurationTests
{
    private static readonly string Folder = Path.Combine(Repository.Root, "src", "Daml.VisualStudio", "LanguageConfiguration");

    private static JObject Configuration() =>
        JObject.Parse(File.ReadAllText(Path.Combine(Folder, "daml-language-configuration.json")));

    [Fact, Trait("Category", "Editor")]
    public void Comments_use_daml_syntax()
    {
        var comments = Configuration()["comments"]!;

        ((string)comments["lineComment"]!).ShouldBe("--");
        comments["blockComment"]!.Values<string>().ShouldBe(["{-", "-}"]);
    }

    [Fact, Trait("Category", "Editor")]
    public void Brackets_and_double_quotes_close_automatically()
    {
        var opens = Configuration()["autoClosingPairs"]!.Select(p => (string)p["open"]!).ToList();

        opens.ShouldBe(["(", "[", "{", "\""], ignoreOrder: true);
    }

    // x' and foldl' are ordinary names; closing the apostrophe would break every one of them.
    [Fact, Trait("Category", "Editor")]
    public void The_apostrophe_never_closes_or_surrounds()
    {
        var configuration = Configuration();

        configuration["autoClosingPairs"]!.ShouldNotContain(p => (string)p["open"]! == "'");
        configuration["surroundingPairs"]!.ShouldNotContain(p => (string)p[0]! == "'");
    }

    [Fact, Trait("Category", "Editor")]
    public void A_selection_can_be_wrapped_in_brackets_or_quotes()
    {
        var pairs = Configuration()["surroundingPairs"]!.Select(p => (string)p[0]! + (string)p[1]!).ToList();

        pairs.ShouldBe(["()", "[]", "{}", "\"\""], ignoreOrder: true);
    }

    [Theory, Trait("Category", "Editor")]
    [InlineData("foldl' f z xs", "foldl'")]
    [InlineData("let x'' = 1", "x''")]
    [InlineData("create_iou with", "create_iou")]
    [InlineData("Iou.amount", "Iou")]
    [InlineData("3.14 + 1", "3.14")]
    public void Double_click_selects_whole_daml_names(string line, string expected)
    {
        var pattern = new Regex((string)Configuration()["wordPattern"]!);
        var start = line.IndexOf(expected, System.StringComparison.Ordinal);

        pattern.Matches(line).Cast<Match>().ShouldContain(m => m.Index == start && m.Value == expected);
    }

    // Registering a TextMate grammar too would colour .daml files a second time, over our classifier.
    [Fact, Trait("Category", "Editor")]
    public void The_pkgdef_maps_the_daml_content_type_and_registers_no_grammar()
    {
        var pkgdef = File.ReadAllLines(Path.Combine(Folder, "DamlLanguageConfiguration.pkgdef"))
            .Where(l => !l.TrimStart().StartsWith(";", System.StringComparison.Ordinal) && l.Trim().Length > 0)
            .ToList();

        pkgdef.ShouldBe(
        [
            @"[$RootKey$\TextMate\LanguageConfiguration\ContentTypeMapping]",
            @"""daml""=""$PackageFolder$\LanguageConfiguration\daml-language-configuration.json""",
        ]);
    }

    [Fact, Trait("Category", "Editor")]
    public void The_pkgdef_is_a_vsix_asset_and_both_files_ship()
    {
        var project = Path.Combine(Repository.Root, "src", "Daml.VisualStudio");
        XNamespace vsx = "http://schemas.microsoft.com/developer/vsx-schema/2011";
        var assets = XDocument.Load(Path.Combine(project, "source.extension.vsixmanifest")).Descendants(vsx + "Asset");
        var shipped = XDocument.Load(Path.Combine(project, "Daml.VisualStudio.csproj")).Descendants("Content")
            .Where(c => (string?)c.Attribute("IncludeInVSIX") == "true")
            .Select(c => (string)c.Attribute("Include")!)
            .ToList();

        assets.ShouldContain(a => (string?)a.Attribute("Type") == "Microsoft.VisualStudio.VsPackage"
            && (string?)a.Attribute("Path") == @"LanguageConfiguration\DamlLanguageConfiguration.pkgdef");
        shipped.ShouldContain(@"LanguageConfiguration\DamlLanguageConfiguration.pkgdef");
        shipped.ShouldContain(@"LanguageConfiguration\daml-language-configuration.json");
    }
}
