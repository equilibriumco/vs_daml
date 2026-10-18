using Microsoft.CodeAnalysis;

namespace Daml.VisualStudio.Roslyn.Bindings;

/// <summary>
/// The Daml runtime types generated code is recognised by, as one compilation sees them. Code is
/// taken for generated only when it uses these very types, never by names that merely look alike.
/// </summary>
internal sealed class DamlRuntimeTypes
{
    private DamlRuntimeTypes(INamedTypeSymbol identifier, INamedTypeSymbol choice, INamedTypeSymbol choiceName, INamedTypeSymbol field, INamedTypeSymbol record, INamedTypeSymbol damlRecord, INamedTypeSymbol variant, INamedTypeSymbol damlEnum)
    {
        Identifier = identifier;
        Choice = choice;
        ChoiceName = choiceName;
        Field = field;
        Record = record;
        DamlRecord = damlRecord;
        Variant = variant;
        DamlEnum = damlEnum;
    }

    /// <summary><c>Identifier</c>, the type of <c>TemplateId</c> and <c>InterfaceId</c>.</summary>
    public INamedTypeSymbol Identifier { get; }

    /// <summary><c>Choice&lt;TOwner, TArgument, TResult&gt;</c>, unbound.</summary>
    public INamedTypeSymbol Choice { get; }

    /// <summary><c>ChoiceName</c>.</summary>
    public INamedTypeSymbol ChoiceName { get; }

    /// <summary><c>DamlFieldAttribute</c>.</summary>
    public INamedTypeSymbol Field { get; }

    /// <summary><c>IDamlRecord&lt;T&gt;</c>, unbound.</summary>
    public INamedTypeSymbol Record { get; }

    /// <summary><c>DamlRecord</c>, what a generic record converts to.</summary>
    public INamedTypeSymbol DamlRecord { get; }

    /// <summary><c>IDamlVariant&lt;T&gt;</c>, unbound.</summary>
    public INamedTypeSymbol Variant { get; }

    /// <summary><c>DamlEnum</c>, what an enum converts to.</summary>
    public INamedTypeSymbol DamlEnum { get; }

    /// <summary>The types in <paramref name="compilation"/>, or null when it doesn't reference the Daml runtime.</summary>
    public static DamlRuntimeTypes? Find(Compilation compilation)
    {
        var identifier = compilation.GetTypeByMetadataName("Daml.Runtime.Data.Identifier");
        var choice = compilation.GetTypeByMetadataName("Daml.Runtime.Commands.Choice`3");
        var choiceName = compilation.GetTypeByMetadataName("Daml.Runtime.Commands.ChoiceName");
        var field = compilation.GetTypeByMetadataName("Daml.Runtime.Data.DamlFieldAttribute");
        var record = compilation.GetTypeByMetadataName("Daml.Runtime.Data.IDamlRecord`1");
        var damlRecord = compilation.GetTypeByMetadataName("Daml.Runtime.Data.DamlRecord");
        var variant = compilation.GetTypeByMetadataName("Daml.Runtime.Data.IDamlVariant`1");
        var damlEnum = compilation.GetTypeByMetadataName("Daml.Runtime.Data.DamlEnum");
        return identifier is null || choice is null || choiceName is null || field is null || record is null || damlRecord is null || variant is null || damlEnum is null
            ? null
            : new DamlRuntimeTypes(identifier, choice, choiceName, field, record, damlRecord, variant, damlEnum);
    }
}
