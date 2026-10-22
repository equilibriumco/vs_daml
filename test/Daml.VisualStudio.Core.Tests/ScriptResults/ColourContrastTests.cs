using System;
using Daml.VisualStudio.Core.ScriptResults;
using FsCheck.Xunit;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.ScriptResults;

public class ColourContrastTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("#000000", "#ffffff", 21.0)]
    [InlineData("#ffffff", "#000000", 21.0)]
    [InlineData("#777777", "#777777", 1.0)]
    [InlineData("#767676", "#ffffff", 4.54)]
    [InlineData("#00bc00", "#ffffff", 2.56)]
    public void The_ratio_is_the_one_wcag_defines(string a, string b, double ratio) =>
        Math.Round(ColourContrast.Ratio(a, b), 2).ShouldBe(ratio);

    [Fact, Trait("Category", "Unit")]
    public void A_colour_that_reads_already_is_left_alone() =>
        ColourContrast.Readable("#cd3131", "#ffffff").ShouldBe("#cd3131");

    [Fact, Trait("Category", "Unit")]
    public void On_a_light_background_a_faint_colour_is_darkened_just_enough()
    {
        var green = ColourContrast.Readable("#00bc00", "#ffffff");

        ColourContrast.Ratio(green, "#ffffff").ShouldBeGreaterThanOrEqualTo(ColourContrast.Minimum);
        ColourContrast.Ratio(green, "#ffffff").ShouldBeLessThan(5.0, "it stays as close to the original as it can");
        Channel(green, 3).ShouldBeGreaterThan(Channel(green, 1), "it is still green");
        Channel(green, 3).ShouldBeGreaterThan(Channel(green, 5));
    }

    [Fact, Trait("Category", "Unit")]
    public void On_a_dark_background_a_dim_colour_is_lightened()
    {
        var red = ColourContrast.Readable("#f14c4c", "#2b2b2b");

        ColourContrast.Ratio(red, "#2b2b2b").ShouldBeGreaterThanOrEqualTo(ColourContrast.Minimum);
        Channel(red, 1).ShouldBeGreaterThanOrEqualTo(0xf1);
        Channel(red, 1).ShouldBeGreaterThan(Channel(red, 3), "it is still red");
    }

    // Black or white always reaches 4.5:1 against any background, so every colour can be made to.
    [Property(MaxTest = 2000), Trait("Category", "Unit")]
    public bool Any_colour_on_any_background_ends_up_readable(byte r, byte g, byte b, byte br, byte bg, byte bb)
    {
        var background = Hex(br, bg, bb);
        return ColourContrast.Ratio(ColourContrast.Readable(Hex(r, g, b), background), background) >= ColourContrast.Minimum;
    }

    private static string Hex(byte r, byte g, byte b) => $"#{r:x2}{g:x2}{b:x2}";

    private static int Channel(string hex, int at) => Convert.ToInt32(hex.Substring(at, 2), 16);
}
