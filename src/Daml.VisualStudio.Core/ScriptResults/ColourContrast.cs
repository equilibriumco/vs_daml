using System;

namespace Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// Contrast between two <c>#rrggbb</c> colours as WCAG 2 measures it, and the nearest colour to a
/// given one that reads on a background.
/// </summary>
internal static class ColourContrast
{
    /// <summary>What WCAG AA asks of normal-sized text.</summary>
    public const double Minimum = 4.5;

    private const int Steps = 100;

    /// <summary>The contrast ratio of two colours, from 1 (the same) to 21 (black on white).</summary>
    public static double Ratio(string a, string b)
    {
        var x = Luminance(a);
        var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>
    /// <paramref name="colour"/> when it reads on <paramref name="background"/>; otherwise the first
    /// colour on the way from it to black or white (whichever stands out more from the background)
    /// that does. Black or white always reaches the minimum, so the result always reads.
    /// </summary>
    public static string Readable(string colour, string background)
    {
        if (Ratio(colour, background) >= Minimum)
        {
            return colour;
        }
        var target = Ratio("#000000", background) >= Ratio("#ffffff", background) ? 0 : 255;
        var (r, g, b) = Channels(colour);
        for (var step = 1; step <= Steps; step++)
        {
            var t = step / (double)Steps;
            var candidate = Hex(Towards(r, target, t), Towards(g, target, t), Towards(b, target, t));
            if (Ratio(candidate, background) >= Minimum)
            {
                return candidate;
            }
        }
        return Hex(target, target, target);
    }

    private static int Towards(int channel, int target, double t) => (int)Math.Round(channel + (target - channel) * t);

    private static string Hex(int r, int g, int b) => $"#{r:x2}{g:x2}{b:x2}";

    private static (int R, int G, int B) Channels(string hex) =>
        (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));

    private static double Luminance(string hex)
    {
        var (r, g, b) = Channels(hex);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    private static double Linear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
