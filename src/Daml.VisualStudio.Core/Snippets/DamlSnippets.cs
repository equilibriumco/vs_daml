using System;
using System.Collections.Generic;
using System.Linq;

namespace Daml.VisualStudio.Core.Snippets;

/// <summary>Where in a file a snippet belongs.</summary>
[Flags]
public enum SnippetPlace
{
    /// <summary>Nowhere a snippet compiles: inside a choice or a function, among the fields after <c>with</c>, and so on.</summary>
    None = 0,

    /// <summary>A declaration of the module, in the first column.</summary>
    TopLevel = 1,

    /// <summary>The declarations of a template or an interface, in the block after its <c>where</c>.</summary>
    ContractBody = 2,
}

/// <summary>A code snippet offered in completion.</summary>
/// <param name="Shortcut">What the snippet is listed and filtered as.</param>
/// <param name="Title">A short name for the completion tooltip.</param>
/// <param name="Description">What the snippet writes.</param>
/// <param name="Body">The text, with fields as <c>${1:default}</c> and the final caret stop as <c>$0</c> or <c>${0:default}</c>; lines end in <c>\n</c>.</param>
/// <param name="Places">Where it compiles, and so where completion offers it.</param>
public sealed record DamlSnippet(string Shortcut, string Title, string Description, string Body, SnippetPlace Places);

/// <summary>The snippets the extension offers. Each one builds with its default values.</summary>
public static class DamlSnippets
{
    /// <summary>Every snippet, in the order completion lists them.</summary>
    public static IReadOnlyList<DamlSnippet> All { get; } =
    [
        new("template", "Template", "A template with a signatory field.",
            "template ${1:MyTemplate}\n" +
            "  with\n" +
            "    ${2:owner} : Party\n" +
            "  where\n" +
            "    signatory ${2:owner}\n" +
            "$0",
            SnippetPlace.TopLevel),
        new("choice", "Choice", "A consuming choice with one argument, inside a template or interface.",
            "choice ${1:MyChoice} : ${2:()}\n" +
            "  with\n" +
            "    ${3:argument} : ${4:Text}\n" +
            "  controller ${5:owner}\n" +
            "  do\n" +
            "    ${0:pure ()}",
            SnippetPlace.ContractBody),
        new("nonconsuming choice", "Nonconsuming choice", "A choice that leaves the contract active.",
            "nonconsuming choice ${1:MyChoice} : ${2:()}\n" +
            "  with\n" +
            "    ${3:argument} : ${4:Text}\n" +
            "  controller ${5:owner}\n" +
            "  do\n" +
            "    ${0:pure ()}",
            SnippetPlace.ContractBody),
        new("interface", "Interface", "An interface with a view type and one method.",
            "interface ${1:MyInterface} where\n" +
            "  viewtype ${2:MyView}\n" +
            "\n" +
            "  ${3:getOwner} : ${4:Party}\n" +
            "$0",
            SnippetPlace.TopLevel),
        new("interface instance", "Interface instance", "An interface instance, inside a template.",
            "interface instance ${1:MyInterface} for ${2:MyTemplate} where\n" +
            "  view = ${3:MyView} ${4:owner}\n" +
            "$0",
            SnippetPlace.ContractBody),
        new("script", "Script", "A Daml Script test that allocates a party.",
            "${1:test} : Script ()\n" +
            "${1:test} = script do\n" +
            "  ${2:alice} <- allocateParty \"${3:Alice}\"\n" +
            "  ${0:pure ()}",
            SnippetPlace.TopLevel),
        new("module", "Module", "A module header.",
            "module ${1:Main} where\n" +
            "\n" +
            "$0",
            SnippetPlace.TopLevel),
    ];

    /// <summary>The snippets that belong at <paramref name="place"/>, in the order completion lists them.</summary>
    public static IReadOnlyList<DamlSnippet> For(SnippetPlace place) =>
        place == SnippetPlace.None ? [] : All.Where(s => (s.Places & place) != 0).ToList();
}
