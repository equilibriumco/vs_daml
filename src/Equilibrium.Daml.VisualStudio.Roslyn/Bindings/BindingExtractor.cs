using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Bindings;

/// <summary>
/// Finds the C# declarations the Daml generator wrote in one document: templates and interfaces
/// with their choices and fields, records, variants with their constructors, enums with theirs.
/// A choice's argument record stands for the choice, a variant constructor's record for the
/// constructor. Nothing is guessed from names alone: hand-written code that merely looks similar
/// yields nothing, and so does a compilation without the Daml runtime.
/// </summary>
public static class BindingExtractor
{
    // What a compilation's types stand for is worked out once for all its documents, and lives as
    // long as the compilation, which never changes.
    private static readonly ConditionalWeakTable<Compilation, Holder> Roles = new();

    /// <summary>The generated declarations in <paramref name="model"/>'s document, in source order.</summary>
    public static IReadOnlyList<GeneratedDeclaration> Extract(SemanticModel model, CancellationToken cancellationToken = default)
    {
        var compilation = (model ?? throw new ArgumentNullException(nameof(model))).Compilation;
        var roles = Roles.GetValue(compilation, c => new Holder(DamlRuntimeTypes.Find(c) is { } runtime ? new GeneratedTypeRoles(c, runtime) : null)).Value;
        return roles is null ? [] : new Reader(model, roles, cancellationToken).Read();
    }

    private sealed class Holder(GeneratedTypeRoles? value)
    {
        public GeneratedTypeRoles? Value { get; } = value;
    }

    private sealed class Reader
    {
        private readonly SemanticModel _model;
        private readonly GeneratedTypeRoles _roles;
        private readonly CancellationToken _cancellationToken;
        private readonly List<GeneratedDeclaration> _found = [];

        public Reader(SemanticModel model, GeneratedTypeRoles roles, CancellationToken cancellationToken)
        {
            _model = model;
            _roles = roles;
            _cancellationToken = cancellationToken;
        }

        public List<GeneratedDeclaration> Read()
        {
            var types = _model.SyntaxTree.GetRoot(_cancellationToken)
                .DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
                .OfType<BaseTypeDeclarationSyntax>();
            foreach (var declaration in types)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (_model.GetDeclaredSymbol(declaration, _cancellationToken) is INamedTypeSymbol type && _roles.Of(type) is { } role)
                {
                    Read(declaration, type, role);
                }
            }
            return _found;
        }

        private void Read(BaseTypeDeclarationSyntax declaration, INamedTypeSymbol type, GeneratedRole role)
        {
            var span = declaration.Identifier.Span;
            switch (role)
            {
                case OwnerRole owner:
                    // A partial template is reported once, from the part with its TemplateId.
                    if (owner.IdProperty.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == _model.SyntaxTree && declaration.Span.Contains(r.Span)))
                    {
                        Add(owner.Kind, owner.PackageId, owner.Module, owner.Entity, null, type, span);
                    }
                    foreach (var (property, at) in Properties(declaration, type))
                    {
                        if (_roles.ChoiceOf(property) is { } choice)
                        {
                            Add(DamlSymbolKind.Choice, owner.PackageId, owner.Module, owner.Entity, choice, property, at);
                        }
                        else if (_roles.FieldOf(property) is { } field)
                        {
                            Add(DamlSymbolKind.Field, owner.PackageId, owner.Module, owner.Entity, field, property, at);
                        }
                    }
                    break;
                case ChoiceArgumentRole argument:
                    Add(DamlSymbolKind.Choice, argument.Owner.PackageId, argument.Owner.Module, argument.Owner.Entity, argument.Choice, type, span);
                    AddFields(declaration, type, argument.Owner.PackageId, argument.Owner.Module, argument.Owner.Entity, argument.Choice + ".");
                    break;
                case ConstructorRole constructor:
                    Add(DamlSymbolKind.Constructor, null, null, constructor.Variant.Entity, constructor.Constructor, type, span);
                    AddFields(declaration, type, null, null, constructor.Variant.Entity, constructor.Constructor + ".");
                    break;
                case DataTypeRole data:
                    Add(data.Kind, null, null, data.Entity, null, type, span);
                    if (data.Kind == DamlSymbolKind.Enum)
                    {
                        AddEnumConstructors(declaration, type, data.Entity);
                    }
                    else
                    {
                        AddFields(declaration, type, null, null, data.Entity, "");
                    }
                    break;
            }
        }

        private void AddFields(BaseTypeDeclarationSyntax declaration, INamedTypeSymbol type, string? package, string? module, string entity, string prefix)
        {
            foreach (var (property, at) in Properties(declaration, type))
            {
                if (_roles.FieldOf(property) is { } field)
                {
                    Add(DamlSymbolKind.Field, package, module, entity, prefix + field, property, at);
                }
            }
        }

        private void AddEnumConstructors(BaseTypeDeclarationSyntax declaration, INamedTypeSymbol type, string entity)
        {
            foreach (var (member, constructor) in _roles.EnumConstructors(type))
            {
                if (member.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == _model.SyntaxTree && declaration.Span.Contains(r.Span))?.GetSyntax()
                    is EnumMemberDeclarationSyntax syntax)
                {
                    Add(DamlSymbolKind.Constructor, null, null, entity, constructor, member, syntax.Identifier.Span);
                }
            }
        }

        // The properties this part of the type declares that can be a field or a choice: written
        // out with an attribute or as a static property built with an object initializer, or from a
        // record's parameters with an attribute. Only those are asked about their symbols, which is
        // most of the time spent here. A parameter with a property of the same name written out is
        // one property.
        private IEnumerable<(IPropertySymbol Property, TextSpan At)> Properties(BaseTypeDeclarationSyntax declaration, INamedTypeSymbol type)
        {
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (declaration is TypeDeclarationSyntax typeDeclaration)
            {
                foreach (var property in typeDeclaration.Members.OfType<PropertyDeclarationSyntax>())
                {
                    if ((property.AttributeLists.Count > 0 || property.Initializer?.Value is BaseObjectCreationExpressionSyntax { Initializer: not null })
                        && _model.GetDeclaredSymbol(property, _cancellationToken) is IPropertySymbol symbol && seen.Add(symbol))
                    {
                        yield return (symbol, property.Identifier.Span);
                    }
                }
            }
            if (declaration is RecordDeclarationSyntax { ParameterList: { } parameters })
            {
                foreach (var parameter in parameters.Parameters.Where(p => p.AttributeLists.Count > 0))
                {
                    if (type.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault() is { } symbol && seen.Add(symbol))
                    {
                        yield return (symbol, parameter.Identifier.Span);
                    }
                }
            }
        }

        private void Add(DamlSymbolKind kind, string? package, string? module, string entity, string? member, ISymbol symbol, TextSpan span)
        {
            if (DocumentationCommentId.CreateDeclarationId(symbol) is not { } id)
            {
                return;
            }
            var @namespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";
            _found.Add(new GeneratedDeclaration(new DeclaredDamlName(kind, package, module, entity, member, @namespace), id, _model.SyntaxTree.FilePath, span));
        }
    }
}
