using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Equilibrium.Daml.Build.Core.Diagnostics;

namespace Equilibrium.Daml.VisualStudio.Core.Build;

/// <summary>
/// A build diagnostic placed the way editors count: 0-based lines and columns, end exclusive.
/// </summary>
/// <param name="FilePath">Full path of the file (or folder, for package-level messages).</param>
/// <param name="Line">First line, 0-based.</param>
/// <param name="Column">First column, 0-based.</param>
/// <param name="EndLine">Last line, 0-based.</param>
/// <param name="EndColumn">Column just past the end, 0-based.</param>
/// <param name="Severity">Severity.</param>
/// <param name="Message">The message.</param>
/// <param name="Source">What reported it, e.g. <c>typecheck</c>.</param>
public sealed record BuildDiagnostic(
    string FilePath, int Line, int Column, int EndLine, int EndColumn, DamlDiagnosticSeverity Severity, string Message, string? Source)
{
    private static readonly char[] Blanks = ['\r', '\n', ' ', '\t'];

    // "daml/TypeError.daml:5:9: error:" in front of damlc's text; the output line already has it.
    private static readonly Regex RepeatedPlace = new(@"^\s*\S.*?:\d+:\d+:\s*(error|warning):\s*", RegexOptions.IgnoreCase);

    /// <summary>Code shown for build errors.</summary>
    public const string ErrorCode = "DAML0001";

    /// <summary>Code shown for build warnings.</summary>
    public const string WarningCode = "DAML0002";

    /// <summary><see cref="ErrorCode"/> or <see cref="WarningCode"/>.</summary>
    public string Code => Severity == DamlDiagnosticSeverity.Error ? ErrorCode : WarningCode;

    /// <summary>
    /// One line in the form Visual Studio's Output window jumps from on double-click:
    /// <c>path(line,column): error DAML0001: message</c>, 1-based, the message on a single line.
    /// </summary>
    public string ToOutputLine()
    {
        var message = string.Join(" ", RepeatedPlace.Replace(Message ?? "", "").Split(Blanks, StringSplitOptions.RemoveEmptyEntries));
        var kind = Severity == DamlDiagnosticSeverity.Error ? "error" : "warning";
        return string.Format(CultureInfo.InvariantCulture, "{0}({1},{2}): {3} {4}: {5}", FilePath, Line + 1, Column + 1, kind, Code, message);
    }

    /// <summary>
    /// Converts damlc's 1-based range (end column just past the end) to 0-based positions and
    /// resolves the file against the folder dpm ran in. No range means the start of the file.
    /// </summary>
    public static BuildDiagnostic From(DamlDiagnostic diagnostic, string workingDirectory)
    {
        if (diagnostic is null)
        {
            throw new ArgumentNullException(nameof(diagnostic));
        }
        var range = diagnostic.Range ?? new DamlRange(1, 1, 1, 1);
        return new BuildDiagnostic(
            diagnostic.ResolvePath(workingDirectory),
            ZeroBased(range.StartLine),
            ZeroBased(range.StartColumn),
            ZeroBased(range.EndLine),
            ZeroBased(range.EndColumn),
            diagnostic.Severity,
            diagnostic.Message,
            diagnostic.Source);
    }

    private static int ZeroBased(int oneBased) => Math.Max(0, oneBased - 1);
}

