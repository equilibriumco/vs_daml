using System;

namespace Daml.VisualStudio.Core.Syntax;

/// <summary>Names of the classification types the editor colours Daml with.</summary>
public static class DamlClassificationNames
{
    /// <summary>Keywords in general: module and import keywords, control flow, True/False.</summary>
    public const string Keyword = "daml.keyword";

    /// <summary>Keywords that introduce declarations: template, data, class, let, ...</summary>
    public const string Declaration = "daml.declaration";

    /// <summary>Contract clauses and choice modifiers: signatory, choice, nonconsuming, ...</summary>
    public const string ContractKeyword = "daml.contractKeyword";

    /// <summary>Capitalised names: types, constructors, modules.</summary>
    public const string Type = "daml.type";

    /// <summary>Line and block comments.</summary>
    public const string Comment = "daml.comment";

    /// <summary>Haddock comments (<c>-- |</c>, <c>-- ^</c>).</summary>
    public const string DocComment = "daml.docComment";

    /// <summary>Compiler pragmas.</summary>
    public const string Pragma = "daml.pragma";

    /// <summary>String and character literals.</summary>
    public const string StringLiteral = "daml.string";

    /// <summary>Numeric literals.</summary>
    public const string Number = "daml.number";

    /// <summary>Operators, reserved symbols, backtick infix names and type application.</summary>
    public const string Operator = "daml.operator";

    /// <summary>Lower-case names: variables, functions, fields.</summary>
    public const string Identifier = "daml.identifier";
}

/// <summary>A coloured piece of one line.</summary>
/// <param name="Start">Offset within the line.</param>
/// <param name="Length">Length in characters.</param>
/// <param name="Classification">One of <see cref="DamlClassificationNames"/>.</param>
public readonly record struct ClassifiedSpan(int Start, int Length, string Classification);

/// <summary>Decides the colour of each token.</summary>
public static class ClassificationMapper
{
    /// <summary>
    /// The classification for <paramref name="token"/> of <paramref name="line"/>, or <c>null</c>
    /// for tokens left in the default text colour (whitespace, brackets, errors).
    /// </summary>
    public static string? Map(DamlToken token, ReadOnlySpan<char> line) => token.Kind switch
    {
        DamlTokenKind.Keyword or DamlTokenKind.ControlKeyword or DamlTokenKind.ModuleKeyword
            or DamlTokenKind.ImportKeyword or DamlTokenKind.Boolean => DamlClassificationNames.Keyword,
        DamlTokenKind.DeclarationKeyword => DamlClassificationNames.Declaration,
        DamlTokenKind.ContractKeyword or DamlTokenKind.ChoiceModifier => DamlClassificationNames.ContractKeyword,
        DamlTokenKind.ConId => DamlClassificationNames.Type,
        DamlTokenKind.VarId => DamlClassificationNames.Identifier,
        DamlTokenKind.QualifiedName => LastSegment(line.Slice(token.Start, token.Length)),
        DamlTokenKind.LineComment or DamlTokenKind.BlockComment => DamlClassificationNames.Comment,
        DamlTokenKind.DocComment => DamlClassificationNames.DocComment,
        DamlTokenKind.Pragma => DamlClassificationNames.Pragma,
        DamlTokenKind.String or DamlTokenKind.Char => DamlClassificationNames.StringLiteral,
        DamlTokenKind.Number => DamlClassificationNames.Number,
        DamlTokenKind.Operator or DamlTokenKind.ReservedOp or DamlTokenKind.Backtick
            or DamlTokenKind.TypeApplication => DamlClassificationNames.Operator,
        _ => null,
    };

    // DA.List.sort is a function, DA.Map.Map a type, DA.Map.! an operator.
    private static string LastSegment(ReadOnlySpan<char> name)
    {
        var dot = name.LastIndexOf('.');
        var segment = dot >= 0 && dot + 1 < name.Length ? name.Slice(dot + 1) : name;
        if (char.IsUpper(segment[0]))
        {
            return DamlClassificationNames.Type;
        }
        return char.IsLetter(segment[0]) || segment[0] == '_' ? DamlClassificationNames.Identifier : DamlClassificationNames.Operator;
    }
}
