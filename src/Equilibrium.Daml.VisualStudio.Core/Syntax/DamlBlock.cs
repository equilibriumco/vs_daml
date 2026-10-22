using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>What a block of a Daml file is.</summary>
public enum DamlBlockKind
{
    /// <summary>The whole file.</summary>
    Module,

    /// <summary>Consecutive <c>import</c> lines.</summary>
    ImportGroup,

    /// <summary><c>template X</c>.</summary>
    Template,

    /// <summary><c>interface X</c>.</summary>
    Interface,

    /// <summary><c>interface instance I for T</c> inside a template or interface.</summary>
    InterfaceInstance,

    /// <summary><c>exception X</c>.</summary>
    Exception,

    /// <summary><c>choice X</c>, with or without a consuming modifier.</summary>
    Choice,

    /// <summary><c>data</c>, <c>newtype</c> or <c>type</c>.</summary>
    Data,

    /// <summary><c>class</c>.</summary>
    Class,

    /// <summary><c>instance</c>.</summary>
    Instance,

    /// <summary>A top-level function or value: its signature and equations together.</summary>
    TopLevelBinding,

    /// <summary>The statements after <c>do</c>.</summary>
    DoBlock,

    /// <summary>The fields after <c>with</c>.</summary>
    WithBlock,

    /// <summary>The declarations after <c>where</c>.</summary>
    WhereBlock,

    /// <summary>The bindings after <c>let</c>.</summary>
    LetBlock,

    /// <summary>The alternatives after <c>of</c> or <c>\case</c>.</summary>
    CaseBlock,

    /// <summary>A <c>{- ... -}</c> comment over several lines.</summary>
    BlockComment,
}

/// <summary>A block of a Daml file, with the blocks inside it. Lines and columns count from zero.</summary>
/// <param name="Kind">What the block is.</param>
/// <param name="Name">The declared name (<c>Asset</c>, <c>Transfer</c>, <c>I for T</c>), if the block has one.</param>
/// <param name="StartLine">The block's first line.</param>
/// <param name="EndLine">The block's last line with code; trailing comments and blank lines are left out.</param>
/// <param name="HeaderEndLine">The last line of the block's header, past brackets left open on its first line.</param>
/// <param name="BodyIndent">The column the block's body is indented to; -1 when it has no body.</param>
/// <param name="Children">The blocks inside this one, in order.</param>
public sealed record DamlBlock(
    DamlBlockKind Kind,
    string? Name,
    int StartLine,
    int EndLine,
    int HeaderEndLine,
    int BodyIndent,
    IReadOnlyList<DamlBlock> Children);
