using System;
using System.Globalization;
using System.Text;

namespace Equilibrium.Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// The editor's look, as the CSS variables the results page reads: the page was written for VS
/// Code and colours itself from <c>--vscode-*</c> variables. The ANSI colours it uses for
/// highlighting start from VS Code's light or dark defaults, picked by how bright the background
/// is, and each is darkened or lightened where it would be too faint to read on the background.
/// </summary>
public sealed class ScriptResultsTheme : IEquatable<ScriptResultsTheme>
{
    private static readonly (string Name, string Dark, string Light)[] Ansi =
    [
        ("Red", "#f14c4c", "#cd3131"),
        ("Green", "#23d18b", "#00bc00"),
        ("Yellow", "#f5f543", "#949800"),
        ("Blue", "#3b8eea", "#0451a5"),
        ("Magenta", "#d670d6", "#bc05bc"),
        ("White", "#e5e5e5", "#555555"),
        ("BrightRed", "#f14c4c", "#cd3131"),
        ("BrightYellow", "#f5f543", "#b5ba00"),
        ("BrightBlue", "#3b8eea", "#0451a5"),
    ];

    /// <summary>Creates a theme.</summary>
    /// <param name="background">Background colour as <c>#rrggbb</c>.</param>
    /// <param name="foreground">Text colour as <c>#rrggbb</c>.</param>
    /// <param name="fontFamily">Font family name.</param>
    /// <param name="fontSizePoints">Font size in points, as Visual Studio gives it.</param>
    public ScriptResultsTheme(string background, string foreground, string fontFamily, double fontSizePoints)
    {
        Background = Hex(background, nameof(background));
        Foreground = Hex(foreground, nameof(foreground));
        // The name ends up inside a quoted CSS string; nothing in it may close the string or the rule.
        FontFamily = Strip(fontFamily ?? throw new ArgumentNullException(nameof(fontFamily)));
        FontSizePoints = fontSizePoints > 0 ? fontSizePoints : throw new ArgumentOutOfRangeException(nameof(fontSizePoints));
    }

    /// <summary>Background colour, <c>#rrggbb</c>.</summary>
    public string Background { get; }

    /// <summary>Text colour, <c>#rrggbb</c>.</summary>
    public string Foreground { get; }

    /// <summary>Font family name, safe to quote in CSS.</summary>
    public string FontFamily { get; }

    /// <summary>Font size in points.</summary>
    public double FontSizePoints { get; }

    /// <summary>True when the background is dark.</summary>
    public bool IsDark => Luminance(Background) < 0.5;

    /// <summary>The stylesheet the page loads as <see cref="ScriptResultsPage.ThemeName"/>.</summary>
    public string ToCss()
    {
        var pixels = Math.Round(FontSizePoints * 96 / 72, 1).ToString(CultureInfo.InvariantCulture);
        // VS Code's link colours; the browser's own blue is unreadable on a dark background.
        var link = ColourContrast.Readable(IsDark ? "#3794ff" : "#006ab1", Background);
        var css = new StringBuilder(":root {\n")
            // Form controls and scrollbars are the browser's; without this they stay light.
            .Append("  color-scheme: ").Append(IsDark ? "dark" : "light").Append(";\n")
            .Append("  --vscode-editor-background: ").Append(Background).Append(";\n")
            .Append("  --vscode-editor-foreground: ").Append(Foreground).Append(";\n")
            .Append("  --vscode-font-family: \"").Append(FontFamily).Append("\", sans-serif;\n")
            .Append("  --vscode-font-size: ").Append(pixels).Append("px;\n")
            // The jumps between contracts in the transaction view use it; VS Code's extension sets it.
            .Append("  --link-color: ").Append(link).Append(";\n");
        foreach (var (name, dark, light) in Ansi)
        {
            css.Append("  --vscode-terminal-ansi").Append(name).Append(": ").Append(ColourContrast.Readable(IsDark ? dark : light, Background)).Append(";\n");
        }
        return css.Append("}\n")
            .Append("body { background: var(--vscode-editor-background); color: var(--vscode-editor-foreground); ")
            .Append("font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); }\n")
            .Append("table, th, td { border-color: var(--vscode-editor-foreground); }\n")
            .Append("a, a:visited { color: ").Append(link).Append("; }\n")
            .ToString();
    }

    /// <inheritdoc />
    public bool Equals(ScriptResultsTheme? other) =>
        other is not null && Background == other.Background && Foreground == other.Foreground &&
        FontFamily == other.FontFamily && FontSizePoints.Equals(other.FontSizePoints);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ScriptResultsTheme);

    /// <inheritdoc />
    public override int GetHashCode() =>
        ((Background.GetHashCode() * 31 + Foreground.GetHashCode()) * 31 + FontFamily.GetHashCode()) * 31 + FontSizePoints.GetHashCode();

    private static string Hex(string? colour, string parameter)
    {
        if (colour is not { Length: 7 } || colour[0] != '#')
        {
            throw new ArgumentException($"'{colour}' isn't a #rrggbb colour.", parameter);
        }
        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(colour[i]))
            {
                throw new ArgumentException($"'{colour}' isn't a #rrggbb colour.", parameter);
            }
        }
        return colour.ToLowerInvariant();
    }

    private static string Strip(string name)
    {
        var safe = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is not ('"' or '\\' or ';' or '{' or '}' or '<' or '>') && !char.IsControl(c))
            {
                safe.Append(c);
            }
        }
        return safe.ToString().Trim();
    }

    private static double Luminance(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }
}
