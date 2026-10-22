using System;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>
/// A Daml declaration as the ledger knows it: the package it was compiled into, its module and
/// entity, and for a choice, field or constructor the member inside that entity. Module and
/// entity alone aren't unique, since two packages, or two versions of one, may declare the same.
/// </summary>
public sealed record DamlSymbolId
{
    /// <summary>Creates the identity of one declaration.</summary>
    /// <param name="packageId">The package ID, as Daml-LF hashes it.</param>
    /// <param name="module">The module, dotted: <c>Kinds.Asset</c>.</param>
    /// <param name="entity">The template, interface or data type in the module.</param>
    /// <param name="member">
    /// The choice, field or constructor for those kinds; a field of a choice argument or of a
    /// variant constructor's record is prefixed with it (<c>Give.newOwner</c>). Null otherwise.
    /// </param>
    /// <param name="kind">What the declaration is.</param>
    /// <exception cref="ArgumentException">A required part is empty, or the member doesn't fit the kind.</exception>
    public DamlSymbolId(string packageId, string module, string entity, string? member, DamlSymbolKind kind)
    {
        PackageId = Required(packageId, nameof(packageId));
        Module = Required(module, nameof(module));
        Entity = Required(entity, nameof(entity));
        var isMember = kind is DamlSymbolKind.Choice or DamlSymbolKind.Field or DamlSymbolKind.Constructor;
        if (isMember && string.IsNullOrWhiteSpace(member))
        {
            throw new ArgumentException($"A {kind} needs the member it names.", nameof(member));
        }
        if (!isMember && member is not null)
        {
            throw new ArgumentException($"A {kind} is an entity of its own and has no member.", nameof(member));
        }
        Member = member;
        Kind = kind;
    }

    /// <summary>The package ID.</summary>
    public string PackageId { get; }

    /// <summary>The module.</summary>
    public string Module { get; }

    /// <summary>The template, interface or data type.</summary>
    public string Entity { get; }

    /// <summary>The choice, field or constructor in the entity, or null.</summary>
    public string? Member { get; }

    /// <summary>What the declaration is.</summary>
    public DamlSymbolKind Kind { get; }

    /// <summary><c>Choice &lt;package&gt;:Kinds.Asset:Asset.Give</c>.</summary>
    public override string ToString() => $"{Kind} {PackageId}:{Module}:{Entity}{(Member is null ? "" : "." + Member)}";

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value;
}
