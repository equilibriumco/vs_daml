namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>What a Daml declaration is, as far as navigation between Daml and C# goes.</summary>
public enum DamlSymbolKind
{
    /// <summary>A template.</summary>
    Template,

    /// <summary>An interface.</summary>
    Interface,

    /// <summary>A choice of a template or an interface; its member is the choice name.</summary>
    Choice,

    /// <summary>
    /// A field: of a template, record or interface view (member <c>x</c>), of a choice's argument
    /// (<c>Give.newOwner</c>) or of a variant constructor's record (<c>Circle.radius</c>).
    /// </summary>
    Field,

    /// <summary>A record data type.</summary>
    Record,

    /// <summary>A variant data type.</summary>
    Variant,

    /// <summary>An enum data type.</summary>
    Enum,

    /// <summary>A constructor of a variant or an enum; its member is the constructor name.</summary>
    Constructor,
}
