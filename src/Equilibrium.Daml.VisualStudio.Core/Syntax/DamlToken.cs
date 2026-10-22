using System.Diagnostics.CodeAnalysis;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>What a stretch of a Daml line is.</summary>
public enum DamlTokenKind
{
    /// <summary>Spaces and tabs.</summary>
    Whitespace,

    /// <summary><c>-- ...</c> to the end of the line.</summary>
    LineComment,

    /// <summary>A documentation comment, <c>-- |</c> or <c>-- ^</c>.</summary>
    DocComment,

    /// <summary>A <c>{- ... -}</c> comment, or the part of one on this line.</summary>
    BlockComment,

    /// <summary>A <c>{-# ... #-}</c> pragma, or the part of one on this line.</summary>
    Pragma,

    /// <summary>A keyword with no more specific kind, such as <c>where</c>.</summary>
    Keyword,

    /// <summary>Keywords introducing declarations: <c>template</c>, <c>data</c>, <c>let</c>, ...</summary>
    DeclarationKeyword,

    /// <summary>Keywords of template and interface bodies: <c>with</c>, <c>choice</c>, <c>signatory</c>, ...</summary>
    ContractKeyword,

    /// <summary><c>nonconsuming</c>, <c>preconsuming</c>, <c>postconsuming</c>.</summary>
    ChoiceModifier,

    /// <summary>Control flow: <c>do</c>, <c>if</c>, <c>case</c>, ...</summary>
    ControlKeyword,

    /// <summary><c>module</c>.</summary>
    ModuleKeyword,

    /// <summary><c>import</c>, <c>qualified</c>, <c>as</c>, <c>hiding</c>.</summary>
    ImportKeyword,

    /// <summary>A capitalised name: type, constructor or module.</summary>
    ConId,

    /// <summary>A lower-case name: variable or function.</summary>
    VarId,

    /// <summary>A name with a module qualifier, such as <c>DA.List.sort</c>.</summary>
    QualifiedName,

    /// <summary>A user-definable operator.</summary>
    Operator,

    /// <summary>An operator with fixed meaning: <c>-&gt;</c>, <c>::</c>, <c>=</c>, ...</summary>
    ReservedOp,

    /// <summary>Brackets, commas and semicolons.</summary>
    Punctuation,

    /// <summary>A string literal, or the part of one on this line.</summary>
    [SuppressMessage("Naming", "CA1720", Justification = "Named after the literal it marks, like the other kinds.")]
    String,

    /// <summary>A character literal.</summary>
    [SuppressMessage("Naming", "CA1720", Justification = "Named after the literal it marks, like the other kinds.")]
    Char,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary><c>True</c> or <c>False</c>.</summary>
    Boolean,

    /// <summary><c>()</c>.</summary>
    Unit,

    /// <summary>A name used as an infix operator, <c>`elem`</c>.</summary>
    Backtick,

    /// <summary>The <c>@</c> of a type application.</summary>
    TypeApplication,

    /// <summary>A character that can't start any token.</summary>
    Error,
}

/// <summary>A token on one line.</summary>
/// <param name="Start">Offset in the line.</param>
/// <param name="Length">Length in UTF-16 code units; never zero.</param>
/// <param name="Kind">What the token is.</param>
public readonly record struct DamlToken(int Start, int Length, DamlTokenKind Kind);
