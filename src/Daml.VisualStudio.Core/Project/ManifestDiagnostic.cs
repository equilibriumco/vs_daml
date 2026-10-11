namespace Daml.VisualStudio.Core.Project;

/// <summary>How serious a manifest problem is.</summary>
public enum ManifestSeverity
{
    /// <summary>The manifest still reads, but something looks off.</summary>
    Warning,

    /// <summary>The manifest (or one of its fields) could not be read.</summary>
    Error,
}

/// <summary>A problem found while reading a manifest, with a 1-based position when one is known.</summary>
/// <param name="Path">Full path of the manifest.</param>
/// <param name="Line">1-based line, or <c>null</c> when the problem is not tied to a place in the file.</param>
/// <param name="Column">1-based column, or <c>null</c> together with <paramref name="Line"/>.</param>
/// <param name="Severity">Error or warning.</param>
/// <param name="Message">What is wrong, for people.</param>
public sealed record ManifestDiagnostic(string Path, int? Line, int? Column, ManifestSeverity Severity, string Message)
{
    /// <summary>Canonical compiler format, so Error List and build logs can point at the spot.</summary>
    public override string ToString()
    {
        var where = Line is { } line ? $"{Path}({line},{Column ?? 1})" : Path;
        return $"{where}: {(Severity == ManifestSeverity.Error ? "error" : "warning")}: {Message}";
    }
}
