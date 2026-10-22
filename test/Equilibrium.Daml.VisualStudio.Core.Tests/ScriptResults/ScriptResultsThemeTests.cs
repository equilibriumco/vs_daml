using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using FsCheck.Xunit;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.ScriptResults;

public class ScriptResultsThemeTests
{
    [Fact, Trait("Category", "Unit")]
    public void The_editor_colours_and_font_become_the_variables_the_page_reads()
    {
        var css = new ScriptResultsTheme("#1e1e1e", "#d4d4d4", "Segoe UI", 9).ToCss();

        css.ShouldContain("--vscode-editor-background: #1e1e1e;");
        css.ShouldContain("--vscode-editor-foreground: #d4d4d4;");
        css.ShouldContain("--vscode-font-family: \"Segoe UI\", sans-serif;");
        css.ShouldContain("--vscode-font-size: 12px;", Case.Sensitive, "9 points is 12 pixels");
    }

    // Left to the browser, links are dark blue: unreadable on a dark background.
    [Theory, Trait("Category", "Unit")]
    [InlineData("#1e1e1e", "#3794ff")]
    [InlineData("#000000", "#3794ff")]
    [InlineData("#ffffff", "#006ab1")]
    public void Links_get_a_colour_that_reads_on_the_background(string background, string link)
    {
        var css = new ScriptResultsTheme(background, "#808080", "Segoe UI", 9).ToCss();

        css.ShouldContain($"a, a:visited {{ color: {link}; }}");
    }

    // VS Code's own palette is where the colours start; where it reads, it is kept.
    [Theory, Trait("Category", "Unit")]
    [InlineData("#1e1e1e", "#f14c4c")]
    [InlineData("#ffffff", "#cd3131")]
    [InlineData("#f5f5f5", "#cd3131")]
    public void The_ansi_palette_follows_the_brightness_of_the_background(string background, string red)
    {
        var css = new ScriptResultsTheme(background, "#808080", "Segoe UI", 9).ToCss();

        css.ShouldContain($"--vscode-terminal-ansiRed: {red};");
        foreach (var name in new[] { "Green", "Yellow", "Blue", "Magenta", "White", "BrightRed", "BrightYellow", "BrightBlue" })
        {
            css.ShouldContain($"--vscode-terminal-ansi{name}: #");
        }
    }

    // VS Code's light keywords (green) and types (bright yellow) are faint on white, its dark red
    // dim on a lighter dark grey; each is moved until it reads.
    [Theory, Trait("Category", "Unit")]
    [InlineData("#ffffff", "Green", "#00bc00")]
    [InlineData("#ffffff", "BrightYellow", "#b5ba00")]
    [InlineData("#2b2b2b", "Red", "#f14c4c")]
    public void A_colour_that_doesnt_read_on_the_background_is_moved_until_it_does(string background, string name, string original)
    {
        var colours = Colours(new ScriptResultsTheme(background, "#808080", "Segoe UI", 9).ToCss());

        colours[$"ansi{name}"].ShouldNotBe(original);
        ColourContrast.Ratio(colours[$"ansi{name}"], background).ShouldBeGreaterThanOrEqualTo(ColourContrast.Minimum);
    }

    [Property(MaxTest = 500), Trait("Category", "Unit")]
    public bool Every_colour_of_the_page_reads_on_any_background(byte r, byte g, byte b)
    {
        var background = $"#{r:x2}{g:x2}{b:x2}";
        var colours = Colours(new ScriptResultsTheme(background, "#808080", "Segoe UI", 9).ToCss());

        return colours.Count == 11 && colours.Values.All(c => ColourContrast.Ratio(c, background) >= ColourContrast.Minimum);
    }

    // The jumps between contracts in the transaction view are coloured with --link-color, which
    // VS Code's extension defines and the page itself doesn't.
    [Theory, Trait("Category", "Unit")]
    [InlineData("#1e1e1e")]
    [InlineData("#ffffff")]
    public void In_page_links_get_the_link_colour(string background)
    {
        var colours = Colours(new ScriptResultsTheme(background, "#808080", "Segoe UI", 9).ToCss());

        colours["link"].ShouldBe(colours["a"]);
    }

    // The checkboxes, the button and the scrollbars are drawn by the browser, light unless told.
    [Theory, Trait("Category", "Unit")]
    [InlineData("#1e1e1e", "dark")]
    [InlineData("#ffffff", "light")]
    public void Controls_and_scrollbars_follow_the_brightness_of_the_background(string background, string scheme) =>
        new ScriptResultsTheme(background, "#808080", "Segoe UI", 9).ToCss().ShouldContain($"  color-scheme: {scheme};\n");

    // Every colour the stylesheet sets, by variable name (without "--vscode-terminal-" or "--"
    // and "-color"), plus "a" for the colour of links.
    private static Dictionary<string, string> Colours(string css)
    {
        var colours = Regex.Matches(css, @"--(?:vscode-terminal-)?(?<name>ansi\w+|link)(?:-color)?: (?<hex>#[0-9a-f]{6});")
            .Cast<Match>()
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["hex"].Value);
        colours["a"] = Regex.Match(css, @"a, a:visited \{ color: (?<hex>#[0-9a-f]{6}); \}").Groups["hex"].Value;
        return colours;
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#gggggg")]
    [InlineData("#000000; } body { display: none")]
    public void A_colour_that_isnt_hex_is_refused(string colour)
    {
        Should.Throw<ArgumentException>(() => new ScriptResultsTheme(colour, "#000000", "Segoe UI", 9));
        Should.Throw<ArgumentException>(() => new ScriptResultsTheme("#000000", colour, "Segoe UI", 9));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_font_name_cant_break_out_of_its_declaration()
    {
        var css = new ScriptResultsTheme("#000000", "#ffffff", "Evil\"; } body { display: none; } a {\\<", 9).ToCss();

        var line = css.Split('\n').Single(l => l.TrimStart().StartsWith("--vscode-font-family:", StringComparison.Ordinal));
        var name = line.Substring(line.IndexOf('"') + 1, line.LastIndexOf('"') - line.IndexOf('"') - 1);
        name.IndexOfAny(['"', ';', '{', '}', '\\', '<', '>']).ShouldBe(-1);
        name.ShouldStartWith("Evil");
        line.ShouldEndWith("\", sans-serif;");
    }

    [Fact, Trait("Category", "Unit")]
    public void Themes_with_the_same_values_are_equal()
    {
        new ScriptResultsTheme("#000000", "#ffffff", "Consolas", 10).ShouldBe(new ScriptResultsTheme("#000000", "#ffffff", "Consolas", 10));
        new ScriptResultsTheme("#000000", "#ffffff", "Consolas", 10).ShouldNotBe(new ScriptResultsTheme("#000001", "#ffffff", "Consolas", 10));
    }
}
