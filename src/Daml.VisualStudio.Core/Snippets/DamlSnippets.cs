using System.Collections.Generic;

namespace Daml.VisualStudio.Core.Snippets;

/// <summary>A code snippet offered in completion.</summary>
/// <param name="Shortcut">What the snippet is listed and filtered as.</param>
/// <param name="Title">A short name for the completion tooltip.</param>
/// <param name="Description">What the snippet writes.</param>
/// <param name="Body">The text, with fields as <c>${1:default}</c> and the final caret stop as <c>$0</c> or <c>${0:default}</c>; lines end in <c>\n</c>.</param>
public sealed record DamlSnippet(string Shortcut, string Title, string Description, string Body);

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
            "$0"),
        new("choice", "Choice", "A consuming choice with one argument, inside a template or interface.",
            "choice ${1:MyChoice} : ${2:()}\n" +
            "  with\n" +
            "    ${3:argument} : ${4:Text}\n" +
            "  controller ${5:owner}\n" +
            "  do\n" +
            "    ${0:pure ()}"),
        new("nonconsuming choice", "Nonconsuming choice", "A choice that leaves the contract active.",
            "nonconsuming choice ${1:MyChoice} : ${2:()}\n" +
            "  with\n" +
            "    ${3:argument} : ${4:Text}\n" +
            "  controller ${5:owner}\n" +
            "  do\n" +
            "    ${0:pure ()}"),
        new("interface", "Interface", "An interface with a view type and one method.",
            "interface ${1:MyInterface} where\n" +
            "  viewtype ${2:MyView}\n" +
            "\n" +
            "  ${3:getOwner} : ${4:Party}\n" +
            "$0"),
        new("interface instance", "Interface instance", "An interface instance, inside a template.",
            "interface instance ${1:MyInterface} for ${2:MyTemplate} where\n" +
            "  view = ${3:MyView} ${4:owner}\n" +
            "$0"),
        new("script", "Script", "A Daml Script test that allocates a party.",
            "${1:test} : Script ()\n" +
            "${1:test} = script do\n" +
            "  ${2:alice} <- allocateParty \"${3:Alice}\"\n" +
            "  ${0:pure ()}"),
        new("module", "Module", "A module header.",
            "module ${1:Main} where\n" +
            "\n" +
            "$0"),
    ];
}
