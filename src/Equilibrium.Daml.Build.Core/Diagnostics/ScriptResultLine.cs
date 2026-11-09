using System.Text.RegularExpressions;

namespace Equilibrium.Daml.Build.Core.Diagnostics;

/// <summary>
/// A line of the summary <c>dpm test</c> prints after the scripts ran. SDK 3.6 sums up each
/// script, <c>daml/Main.daml:setup: ok, 1 active contracts, 3 transactions.</c> or
/// <c>daml/Main.daml:failing: failed</c>; SDK 3.7 sums up the package and each file and names only
/// the scripts that failed: <c>Test Summary (contracts): 1 failed, 1 passed</c>,
/// <c>daml/Main.daml: 1 test failed: failing</c>, <c>daml/Main.daml: 1 test passed</c>.
/// </summary>
public static class ScriptResultLine
{
    private static readonly Regex[] Shapes =
    [
        new(@"^\S[^:]*\.daml:[^\s:]+: (ok, .*|failed)$", RegexOptions.CultureInvariant),
        new(@"^Test Summary \([^)]+\): \d+ (passed|failed)(, \d+ (passed|failed))*$", RegexOptions.CultureInvariant),
        new(@"^\S[^:]*\.daml: \d+ tests? (passed|failed: .+)$", RegexOptions.CultureInvariant),
    ];

    /// <summary>The line without colours and trailing blanks, or null for any other line.</summary>
    public static string? TryParse(string? line)
    {
        if (line is null)
        {
            return null;
        }
        var text = DamlDiagnosticParser.StripAnsi(line).TrimEnd();
        foreach (var shape in Shapes)
        {
            if (shape.IsMatch(text))
            {
                return text;
            }
        }
        return null;
    }
}
