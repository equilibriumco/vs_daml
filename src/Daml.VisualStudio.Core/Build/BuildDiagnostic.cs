using System;
using Daml.Build.Core.Diagnostics;

namespace Daml.VisualStudio.Core.Build;

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

