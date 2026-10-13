using System;
using System.IO;

namespace Daml.Build.Core.Diagnostics;

/// <summary>How serious a diagnostic is.</summary>
public enum DamlDiagnosticSeverity
{
    /// <summary>The build fails because of it.</summary>
    Error,

    /// <summary>Worth fixing, but the build goes on.</summary>
    Warning,

    /// <summary>For information only.</summary>
    Information,

    /// <summary>A suggestion.</summary>
    Hint,
}

/// <summary>
/// Where a diagnostic is, as damlc prints it: 1-based lines and columns, with the end column
/// pointing just past the last character (<c>5:9-5:20</c> covers columns 9 to 19).
/// </summary>
/// <param name="StartLine">First line, 1-based.</param>
/// <param name="StartColumn">First column, 1-based.</param>
/// <param name="EndLine">Last line, 1-based.</param>
/// <param name="EndColumn">Column just past the end, 1-based.</param>
public readonly record struct DamlRange(int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>One diagnostic block from <c>dpm build</c> or <c>dpm test</c>.</summary>
public sealed record DamlDiagnostic
{
    /// <summary>
    /// The file as printed: usually relative to the package root (<c>daml/Main.daml</c>), but
    /// sometimes a folder, an absolute path with forward slashes, or a stand-in like
    /// <c>&lt;unknown&gt;</c> for a package-level message. Use <see cref="ResolvePath"/>.
    /// </summary>
    public required string PrintedPath { get; init; }

    /// <summary>
    /// The package a multi-package build was building when it printed this, as a Windows path;
    /// <c>null</c> in a single-package build, where paths are relative to the working folder.
    /// </summary>
    public string? PackageRoot { get; init; }

    /// <summary>Where in the file; <c>null</c> when damlc printed no usable range.</summary>
    public DamlRange? Range { get; init; }

    /// <summary>Severity; anything unrecognised counts as an error, so it can't be overlooked.</summary>
    public DamlDiagnosticSeverity Severity { get; init; } = DamlDiagnosticSeverity.Error;

    /// <summary>What reported it, e.g. <c>parser</c> or <c>typecheck</c>.</summary>
    public string? Source { get; init; }

    /// <summary>damlc's own "hidden" flag.</summary>
    public bool Hidden { get; init; }

    /// <summary>The message, lines joined with <c>\n</c>, trailing whitespace removed.</summary>
    public string Message { get; init; } = "";

    /// <summary>
    /// The full Windows path of <see cref="PrintedPath"/>: as is when absolute, otherwise under
    /// <see cref="PackageRoot"/> (itself under <paramref name="workingDirectory"/>, the folder dpm
    /// ran in, when relative). When damlc printed no real file, the package folder stands in.
    /// </summary>
    public string ResolvePath(string workingDirectory)
    {
        var package = Path.Combine(workingDirectory, PackageRoot ?? "");
        return (HasFile ? TryFullPath(Path.Combine(package, PrintedPath.Replace('/', '\\'))) : null)
            ?? TryFullPath(package)
            ?? Path.GetFullPath(workingDirectory);
    }

    /// <summary>False when damlc printed a stand-in such as <c>&lt;unknown&gt;</c> instead of a file.</summary>
    public bool HasFile => PrintedPath.Length > 0 && PrintedPath[0] != '<';

    // Null for text the file system refuses as a path (illegal characters, too long).
    private static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
