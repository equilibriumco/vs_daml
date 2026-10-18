using System;
using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Syntax;

namespace Daml.VisualStudio.Core.Bindings;

/// <summary>A declaration a "C#: N references" row sits above: a template, an interface, or a choice of either.</summary>
/// <param name="Kind">Template, interface or choice.</param>
/// <param name="Entity">The template or interface.</param>
/// <param name="Member">The choice; null for the template or interface itself.</param>
/// <param name="Line">The line that declares it, from zero.</param>
public sealed record DamlLensDeclaration(DamlSymbolKind Kind, string Entity, string? Member, int Line)
{
    /// <summary>How the index and the user name it: <c>Asset</c>, <c>Asset.Give</c>.</summary>
    public string Name => Member is null ? Entity : Entity + "." + Member;
}

/// <summary>Finds the templates, interfaces and choices of a Daml file, as the editor's layout analysis sees it.</summary>
public static class DamlLensDeclarations
{
    /// <summary>The module <paramref name="text"/> declares, and its templates and interfaces, each followed by its choices.</summary>
    public static (string? Module, IReadOnlyList<DamlLensDeclaration> Declarations) Of(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        var module = LayoutAnalyzer.Analyze(LineTokens.Lex(text));
        var declarations = new List<DamlLensDeclaration>();
        foreach (var owner in module.Children.Where(b => b.Kind is DamlBlockKind.Template or DamlBlockKind.Interface && b.Name is not null))
        {
            declarations.Add(new DamlLensDeclaration(owner.Kind == DamlBlockKind.Template ? DamlSymbolKind.Template : DamlSymbolKind.Interface, owner.Name!, null, owner.StartLine));
            declarations.AddRange(Descendants(owner)
                .Where(b => b.Kind == DamlBlockKind.Choice && b.Name is not null)
                .Select(choice => new DamlLensDeclaration(DamlSymbolKind.Choice, owner.Name!, choice.Name, choice.StartLine)));
        }
        return (ModuleHeader.DeclaredModule(text), declarations);
    }

    private static IEnumerable<DamlBlock> Descendants(DamlBlock block) =>
        block.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
