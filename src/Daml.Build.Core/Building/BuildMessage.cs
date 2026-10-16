using Daml.Build.Core.Diagnostics;

namespace Daml.Build.Core.Building;

/// <summary>
/// Something a build step reports, shaped for MSBuild's logger: 1-based positions, 0 where
/// there is none, and the range as damlc prints it.
/// </summary>
/// <param name="Severity">Errors fail the build; information and hints are plain messages.</param>
/// <param name="Code">The diagnostic code; null for plain messages.</param>
/// <param name="Subcategory">What reported it, e.g. <c>typecheck</c>; null when nothing more specific than the code applies.</param>
/// <param name="File">The full path it points at: a source file, a folder for package-level messages, or the manifest.</param>
/// <param name="Line">First line, 1-based; 0 for the whole file.</param>
/// <param name="Column">First column, 1-based; 0 for none.</param>
/// <param name="EndLine">Last line, 1-based; 0 for none.</param>
/// <param name="EndColumn">Column just past the end, 1-based; 0 for none.</param>
/// <param name="Text">The message; may span lines when it quotes a tool's output.</param>
public sealed record BuildMessage(
    DamlDiagnosticSeverity Severity,
    string? Code,
    string? Subcategory,
    string File,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Text);
