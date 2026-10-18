using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Daml.VisualStudio.Roslyn.Bindings;

/// <summary>What a generated C# type stands for in Daml.</summary>
internal abstract record GeneratedRole;

/// <summary>A template or interface: its code states package, module and entity.</summary>
internal sealed record OwnerRole(DamlSymbolKind Kind, string PackageId, string Module, string Entity, IPropertySymbol IdProperty) : GeneratedRole;

/// <summary>A record, variant or enum, which names only itself.</summary>
internal sealed record DataTypeRole(DamlSymbolKind Kind, string Entity) : GeneratedRole;

/// <summary>The argument record of a template's or interface's choice.</summary>
internal sealed record ChoiceArgumentRole(OwnerRole Owner, string Choice) : GeneratedRole;

/// <summary>A variant constructor: the nested type, or the record it carries.</summary>
internal sealed record ConstructorRole(DataTypeRole Variant, string Constructor) : GeneratedRole;

/// <summary>
/// Tells generated types apart within one compilation, by the Daml runtime types they use and the
/// names the generator wrote into them: <c>TemplateId</c>/<c>InterfaceId</c>, <c>ChoiceName</c>,
/// <c>DamlFieldAttribute</c>, a variant constructor's <c>Tag</c>, an enum's <c>ToDamlEnum</c>.
/// </summary>
internal sealed class GeneratedTypeRoles
{
    private readonly Compilation _compilation;
    private readonly DamlRuntimeTypes _runtime;
    private readonly Dictionary<ISymbol, GeneratedRole?> _roles = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, OwnerRole?> _owners = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, Dictionary<ISymbol, GeneratedRole>> _namespaces = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, string?> _choices = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

    // One instance serves every document of a compilation, read from any thread.
    private readonly object _gate = new();

    public GeneratedTypeRoles(Compilation compilation, DamlRuntimeTypes runtime)
    {
        _compilation = compilation;
        _runtime = runtime;
    }

    /// <summary>What <paramref name="type"/> stands for, or null when the generator didn't write it.</summary>
    public GeneratedRole? Of(INamedTypeSymbol type)
    {
        lock (_gate)
        {
            type = type.OriginalDefinition;
            if (!_roles.TryGetValue(type, out var role))
            {
                role = Classify(type);
                _roles[type] = role;
            }
            return role;
        }
    }

    /// <summary>The choice a static <c>Choice&lt;…&gt;</c> property declares, or null.</summary>
    public string? ChoiceOf(IPropertySymbol property)
    {
        if (!property.IsStatic || property.Type is not INamedTypeSymbol type || !Same(type.OriginalDefinition, _runtime.Choice))
        {
            return null;
        }
        lock (_gate)
        {
            if (!_choices.TryGetValue(property, out var choice))
            {
                choice = ChoiceNameOf(property);
                _choices[property] = choice;
            }
            return choice;
        }
    }

    // Choice<…> ChoiceGive { get; } = new() { Name = new ChoiceName("Give"), … };
    private string? ChoiceNameOf(IPropertySymbol property)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not PropertyDeclarationSyntax { Initializer.Value: BaseObjectCreationExpressionSyntax { Initializer: { } initializer } })
            {
                continue;
            }
            foreach (var assignment in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
            {
                if (assignment is { Left: IdentifierNameSyntax { Identifier.ValueText: "Name" }, Right: BaseObjectCreationExpressionSyntax creation }
                    && StringArgument(creation, 0) is { } name
                    && Same(CreatedType(creation), _runtime.ChoiceName))
                {
                    return name;
                }
            }
        }
        return null;
    }

    /// <summary>The Daml field a property carries in <c>DamlFieldAttribute</c>, or null.</summary>
    public string? FieldOf(IPropertySymbol property) =>
        property.GetAttributes().FirstOrDefault(a => Same(a.AttributeClass, _runtime.Field))?.ConstructorArguments is [{ Value: string name }, ..] ? name : null;

    /// <summary>The members of a generated enum with the Daml constructor each converts to.</summary>
    public IReadOnlyList<(IFieldSymbol Member, string Constructor)> EnumConstructors(INamedTypeSymbol type)
    {
        var constructors = new List<(IFieldSymbol, string)>();
        lock (_gate)
        {
            foreach (var reference in ToDamlEnum(type)?.DeclaringSyntaxReferences ?? [])
            {
                var model = Model(reference.SyntaxTree);
                foreach (var arm in reference.GetSyntax().DescendantNodes().OfType<SwitchExpressionArmSyntax>())
                {
                    if (arm is { Pattern: ConstantPatternSyntax pattern, Expression: InvocationExpressionSyntax { ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax literal }] } }
                        && literal.IsKind(SyntaxKind.StringLiteralExpression)
                        && model.GetSymbolInfo(pattern.Expression).Symbol is IFieldSymbol member
                        && Same(member.ContainingType, type))
                    {
                        constructors.Add((member, literal.Token.ValueText));
                    }
                }
            }
        }
        return constructors;
    }

    private GeneratedRole? Classify(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return ToDamlEnum(type) is null ? null : new DataTypeRole(DamlSymbolKind.Enum, type.Name);
        }
        if (OwnerOf(type) is { } owner)
        {
            return owner;
        }
        if (IsVariant(type))
        {
            return new DataTypeRole(DamlSymbolKind.Variant, type.Name);
        }
        if (type.ContainingType is { } outer && IsVariant(outer) && Same(type.BaseType?.OriginalDefinition, outer))
        {
            return new ConstructorRole(new DataTypeRole(DamlSymbolKind.Variant, outer.Name), Tag(type) ?? type.Name);
        }
        if (!IsRecord(type))
        {
            return null;
        }
        return InNamespace(type) ?? new DataTypeRole(DamlSymbolKind.Record, type.Name);
    }

    private OwnerRole? OwnerOf(INamedTypeSymbol type)
    {
        if (!_owners.TryGetValue(type, out var owner))
        {
            owner = IdOf(type, "TemplateId", DamlSymbolKind.Template) ?? IdOf(type, "InterfaceId", DamlSymbolKind.Interface);
            _owners[type] = owner;
        }
        return owner;
    }

    // public static Identifier TemplateId { get; } = new("<package>", "<module>", "<entity>");
    private OwnerRole? IdOf(INamedTypeSymbol type, string name, DamlSymbolKind kind)
    {
        foreach (var property in type.GetMembers(name).OfType<IPropertySymbol>().Where(p => p.IsStatic && Same(p.Type, _runtime.Identifier)))
        {
            foreach (var reference in property.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is PropertyDeclarationSyntax { Initializer.Value: BaseObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 3 } creation }
                    && StringArgument(creation, 0) is { } package
                    && StringArgument(creation, 1) is { } module
                    && StringArgument(creation, 2) is { } entity)
                {
                    return new OwnerRole(kind, package, module, entity, property);
                }
            }
        }
        return null;
    }

    // Choice arguments and variant payloads are named by the template, interface or variant that
    // uses them, which is in the same namespace; each namespace is looked through once.
    private GeneratedRole? InNamespace(INamedTypeSymbol type)
    {
        if (!_namespaces.TryGetValue(type.ContainingNamespace, out var roles))
        {
            roles = new Dictionary<ISymbol, GeneratedRole>(SymbolEqualityComparer.Default);
            foreach (var candidate in type.ContainingNamespace.GetTypeMembers())
            {
                AddChoiceArguments(candidate, roles);
                AddPayloads(candidate, roles);
            }
            _namespaces[type.ContainingNamespace] = roles;
        }
        return roles.TryGetValue(type, out var role) ? role : null;
    }

    private void AddChoiceArguments(INamedTypeSymbol candidate, Dictionary<ISymbol, GeneratedRole> roles)
    {
        if (OwnerOf(candidate) is not { } owner)
        {
            return;
        }
        foreach (var property in candidate.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.Type is INamedTypeSymbol { TypeArguments: [_, INamedTypeSymbol argument, _] } && ChoiceOf(property) is { } choice)
            {
                roles[argument.OriginalDefinition] = new ChoiceArgumentRole(owner, choice);
            }
        }
    }

    // public sealed record Circle(Shape_Circle Value) : Shape — Shape_Circle is the constructor's record.
    private void AddPayloads(INamedTypeSymbol candidate, Dictionary<ISymbol, GeneratedRole> roles)
    {
        if (!IsVariant(candidate))
        {
            return;
        }
        foreach (var nested in candidate.GetTypeMembers().Where(n => Same(n.BaseType?.OriginalDefinition, candidate)))
        {
            if (nested.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 1)?.Parameters[0].Type is INamedTypeSymbol payload
                && IsRecord(payload))
            {
                roles[payload.OriginalDefinition] = new ConstructorRole(new DataTypeRole(DamlSymbolKind.Variant, candidate.Name), Tag(nested) ?? nested.Name);
            }
        }
    }

    private bool IsVariant(INamedTypeSymbol type) => type.IsAbstract && Implements(type, _runtime.Variant);

    // A generic record cant implement IDamlRecord<T>: it converts with a converter per type parameter,
    // public DamlRecord ToRecord(Func<TA, DamlValue> convertTA).
    private bool IsRecord(INamedTypeSymbol type) =>
        Implements(type, _runtime.Record)
        || (type.IsRecord && type.IsGenericType && type.GetMembers("ToRecord").OfType<IMethodSymbol>().Any(m => Same(m.ReturnType, _runtime.DamlRecord)));

    // public static DamlEnum ToDamlEnum(this Colour value), in ColourExtensions next to the enum.
    private IMethodSymbol? ToDamlEnum(INamedTypeSymbol type) =>
        type.ContainingNamespace.GetTypeMembers(type.Name + "Extensions")
            .SelectMany(e => e.GetMembers("ToDamlEnum").OfType<IMethodSymbol>())
            .FirstOrDefault(m => m.IsExtensionMethod && m.Parameters.Length == 1 && Same(m.Parameters[0].Type, type) && Same(m.ReturnType, _runtime.DamlEnum));

    // public override string Tag => "Circle";
    private static string? Tag(INamedTypeSymbol constructor) =>
        constructor.GetMembers("Tag").OfType<IPropertySymbol>()
            .SelectMany(p => p.DeclaringSyntaxReferences)
            .Select(r => r.GetSyntax())
            .OfType<PropertyDeclarationSyntax>()
            .Select(p => p.ExpressionBody?.Expression as LiteralExpressionSyntax)
            .FirstOrDefault(l => l is not null && l.IsKind(SyntaxKind.StringLiteralExpression))?.Token.ValueText;

    private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol generic) =>
        type.AllInterfaces.Any(i => Same(i.OriginalDefinition, generic) && i.TypeArguments.Length == 1 && Same(i.TypeArguments[0].OriginalDefinition, type.OriginalDefinition));

    private static string? StringArgument(BaseObjectCreationExpressionSyntax creation, int index) =>
        creation.ArgumentList is { } list && index < list.Arguments.Count
        && list.Arguments[index].Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    // The type a `new` creates. Asking for the type of the whole expression binds the initializer it
    // sits in, with the lambdas a choice's initializer has; binding just the written type name in
    // its place is enough, and far cheaper.
    private ITypeSymbol? CreatedType(BaseObjectCreationExpressionSyntax creation) =>
        creation is ObjectCreationExpressionSyntax { Type: { } type }
            ? Model(creation.SyntaxTree).GetSpeculativeTypeInfo(type.SpanStart, type, SpeculativeBindingOption.BindAsTypeOrNamespace).Type
            : Model(creation.SyntaxTree).GetTypeInfo(creation).Type;

    private SemanticModel Model(SyntaxTree tree)
    {
        if (!_models.TryGetValue(tree, out var model))
        {
            model = _compilation.GetSemanticModel(tree);
            _models[tree] = model;
        }
        return model;
    }

    private static bool Same(ISymbol? a, ISymbol? b) => SymbolEqualityComparer.Default.Equals(a, b);
}
