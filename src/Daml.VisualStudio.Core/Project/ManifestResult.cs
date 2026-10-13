using System.Collections.Generic;
using System.Linq;

namespace Daml.VisualStudio.Core.Project;

/// <summary>
/// Outcome of reading a manifest. <see cref="Value"/> is <c>null</c> when the file could not be read
/// as a manifest at all; otherwise it holds whatever was readable, and <see cref="Diagnostics"/>
/// says which fields were not.
/// </summary>
/// <typeparam name="T">The manifest type.</typeparam>
public sealed class ManifestResult<T>
    where T : class
{
    /// <summary>Creates a result; also for fakes of <see cref="IManifestReader"/> in tests.</summary>
    public ManifestResult(T? value, IReadOnlyList<ManifestDiagnostic> diagnostics)
    {
        Value = value;
        Diagnostics = diagnostics;
    }

    /// <summary>The manifest, possibly with some fields left empty; <c>null</c> if unreadable.</summary>
    public T? Value { get; }

    /// <summary>Problems found while reading, in file order.</summary>
    public IReadOnlyList<ManifestDiagnostic> Diagnostics { get; }

    /// <summary>True when at least one diagnostic is an error.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == ManifestSeverity.Error);
}
