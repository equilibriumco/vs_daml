using System;
using System.Collections.Generic;
using System.Linq;
using Daml.Build.Core.Diagnostics;
using Daml.VisualStudio.Core.FileSystem;

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

/// <summary>
/// The diagnostics of the last dpm command, as shown in the Error List. Safe to use from any
/// thread; <see cref="Changed"/> is raised on the thread that made the change.
/// </summary>
public sealed class BuildDiagnosticStore
{
    private readonly object _gate = new();
    private List<BuildDiagnostic> _current = [];
    private int _version;

    /// <summary>Raised after every change.</summary>
    public event EventHandler? Changed;

    /// <summary>The diagnostics now listed.</summary>
    public IReadOnlyList<BuildDiagnostic> Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Increases with every change.</summary>
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>Replaces everything listed.</summary>
    public void Replace(IEnumerable<BuildDiagnostic> diagnostics)
    {
        var list = (diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))).ToList();
        lock (_gate)
        {
            _current = list;
            _version++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes everything listed.</summary>
    public void Clear() => Replace([]);

    /// <summary>
    /// Removes the entries of one file, e.g. once it's edited and they may no longer be true.
    /// Returns whether anything was removed.
    /// </summary>
    public bool ClearFile(string filePath)
    {
        lock (_gate)
        {
            var kept = _current.Where(d => !DamlPath.Comparer.Equals(d.FilePath, filePath)).ToList();
            if (kept.Count == _current.Count)
            {
                return false;
            }
            _current = kept;
            _version++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
