using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.Project;

/// <summary>Outcome of resolving a module name to a source file.</summary>
public enum ModuleResolutionStatus
{
    /// <summary>Exactly one file declares the module.</summary>
    Found,

    /// <summary>No candidate file exists.</summary>
    Missing,

    /// <summary>More than one package offers a file declaring the module.</summary>
    Ambiguous,

    /// <summary>A file sits where the module should be, but declares something else (renamed or not yet saved).</summary>
    Stale,
}

/// <summary>A file that was checked for a module.</summary>
/// <param name="PackageRoot">Package the file belongs to.</param>
/// <param name="ModuleName">Module name inside that package (without any import prefix).</param>
/// <param name="Path">Full path of the file.</param>
/// <param name="DeclaredModule">What the file's header declares, or <c>null</c> if it has none.</param>
public sealed record ModuleCandidate(string PackageRoot, string ModuleName, string Path, string? DeclaredModule);

/// <summary>Where a module lives, or why that can't be said.</summary>
public sealed record ModuleResolution
{
    /// <summary>The verdict.</summary>
    public required ModuleResolutionStatus Status { get; init; }

    /// <summary>The source file; set only when <see cref="Status"/> is <see cref="ModuleResolutionStatus.Found"/>.</summary>
    public string? Path { get; init; }

    /// <summary>Package holding <see cref="Path"/>.</summary>
    public string? PackageRoot { get; init; }

    /// <summary>Module name inside that package, after removing an import prefix.</summary>
    public string? ModuleName { get; init; }

    /// <summary>
    /// The files behind the verdict: the matching ones for Ambiguous, the mismatching ones for
    /// Stale, the match for Found; empty for Missing.
    /// </summary>
    public IReadOnlyList<ModuleCandidate> Candidates { get; init; } = [];

    /// <summary>Why nothing was found, for people; set for Missing.</summary>
    public string? Reason { get; init; }
}
