using System;
using System.Collections.Generic;
using Daml.Build.Core.Codegen;

namespace Daml.VisualStudio.Core.Bindings;

/// <summary>What became of a <see cref="DeclaredDamlName"/>: the Daml symbol, or why there is none.</summary>
/// <param name="Symbol">The symbol; null when the declaration can't be trusted to stand for one.</param>
/// <param name="Problem">Why not, when <paramref name="Symbol"/> is null.</param>
public sealed record DamlNameResolution(DamlSymbolId? Symbol, string? Problem);

/// <summary>
/// Where a generation came from, as its manifest records it: the DAR's main package, which the
/// generator writes C# for, the namespace prefix it put that C# under, the project that owns it
/// and the Daml sources the DAR was built from. The only grounds for tying generated C# to Daml.
/// </summary>
/// <param name="Folder">The generation's folder.</param>
/// <param name="GenerationId">The generation's identity.</param>
/// <param name="MainPackageId">The package the C# was generated for.</param>
/// <param name="DependencyPackageIds">The other packages in the DAR.</param>
/// <param name="Namespace">The namespace prefix; null for none.</param>
/// <param name="Owner">The project that generated it.</param>
/// <param name="Reference">The Daml project or DAR it was generated from.</param>
/// <param name="ReferenceKind">Which of the two.</param>
/// <param name="Source">The Daml sources a project's DAR was built from; null for a DAR reference or when unknown.</param>
public sealed record GenerationProvenance(
    string Folder,
    string GenerationId,
    string MainPackageId,
    IReadOnlyList<string> DependencyPackageIds,
    string? Namespace,
    string Owner,
    string Reference,
    DamlReferenceKind ReferenceKind,
    SourceProvenance? Source)
{
    /// <summary>The provenance <paramref name="manifest"/> records; null when it doesn't record the DAR's package.</summary>
    public static GenerationProvenance? From(string folder, GenerationManifest manifest) =>
        manifest?.Package is { } package
            ? new GenerationProvenance(folder, manifest.GenerationId, package.MainPackageId, package.DependencyPackageIds, manifest.Namespace,
                manifest.Owner, manifest.Reference, manifest.ReferenceKind, manifest.Source)
            : null;

    /// <summary>
    /// The symbol a declaration of this generation stands for. A data type is of the main package,
    /// in the module its namespace names under the prefix. A template or interface states its own
    /// package and module, which have to be this generation's and its namespace; code that says
    /// otherwise is from another build than the manifest.
    /// </summary>
    public DamlNameResolution Resolve(DeclaredDamlName name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }
        if (name.PackageId is null || name.Module is null)
        {
            return ModuleOf(name.Namespace) is { } module
                ? new DamlNameResolution(new DamlSymbolId(MainPackageId, module, name.Entity, name.Member, name.Kind), null)
                : new DamlNameResolution(null, $"{name.Entity} is in namespace '{name.Namespace}', which isn't under this generation's namespace '{Namespace}'.");
        }
        if (!string.Equals(name.PackageId, MainPackageId, StringComparison.Ordinal))
        {
            return new DamlNameResolution(null, $"{name.Entity} states package {name.PackageId}, but this generation is of package {MainPackageId}; the C# is from another build.");
        }
        if (!string.Equals(ModuleOf(name.Namespace), name.Module, StringComparison.Ordinal))
        {
            return new DamlNameResolution(null, $"{name.Entity} states module {name.Module}, but is in namespace '{name.Namespace}' of this generation.");
        }
        return new DamlNameResolution(new DamlSymbolId(name.PackageId, name.Module, name.Entity, name.Member, name.Kind), null);
    }

    // The generator puts module M in namespace M, or in P.M under the prefix P.
    private string? ModuleOf(string @namespace)
    {
        if (Namespace is null)
        {
            return @namespace.Length > 0 ? @namespace : null;
        }
        return @namespace.Length > Namespace.Length + 1 && @namespace.StartsWith(Namespace + ".", StringComparison.Ordinal)
            ? @namespace.Substring(Namespace.Length + 1)
            : null;
    }
}
