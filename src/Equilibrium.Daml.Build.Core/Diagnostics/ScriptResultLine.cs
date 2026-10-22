using System.Text.RegularExpressions;

namespace Equilibrium.Daml.Build.Core.Diagnostics;

/// <summary>
/// The line <c>dpm test</c> sums up one script with under "Test Summary":
/// <c>daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.</c> or <c>daml/Main.daml:failing: failed</c>.
/// </summary>
public static class ScriptResultLine
{
    private static readonly Regex Shape = new(@"^\S[^:]*\.daml:[^\s:]+: (ok, .*|failed)$", RegexOptions.CultureInvariant);

    /// <summary>The line without colours and trailing blanks, or null for any other line.</summary>
    public static string? TryParse(string? line)
    {
        if (line is null)
        {
            return null;
        }
        var text = DamlDiagnosticParser.StripAnsi(line).TrimEnd();
        return Shape.IsMatch(text) ? text : null;
    }
}
