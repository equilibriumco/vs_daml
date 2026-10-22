using System.Text.RegularExpressions;

namespace Equilibrium.Daml.Build.Core.Diagnostics;

/// <summary>The line <c>dpm build</c> ends a successful build with: <c>Created .daml\dist\name-version.dar</c>.</summary>
public static class DarCreatedLine
{
    private static readonly Regex Shape = new(@"^Created (?<dar>\S.*\.dar)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The DAR the line names, as printed (relative to the package root unless dpm printed a full path), or null for any other line.</summary>
    public static string? TryParse(string? line)
    {
        if (line is null)
        {
            return null;
        }
        var match = Shape.Match(DamlDiagnosticParser.StripAnsi(line));
        return match.Success ? match.Groups["dar"].Value : null;
    }
}
