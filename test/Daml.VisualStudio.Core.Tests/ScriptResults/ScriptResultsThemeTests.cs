using System;
using System.Linq;
using Daml.VisualStudio.Core.ScriptResults;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.ScriptResults;

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

    [Theory, Trait("Category", "Unit")]
    [InlineData("#1e1e1e", "#f14c4c")]
    [InlineData("#252526", "#f14c4c")]
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
