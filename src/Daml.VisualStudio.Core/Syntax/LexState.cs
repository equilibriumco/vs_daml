namespace Daml.VisualStudio.Core.Syntax;

/// <summary>What the lexer is in the middle of when a line ends.</summary>
public enum LexMode
{
    /// <summary>Ordinary code.</summary>
    Normal,

    /// <summary>Inside a <c>{- ... -}</c> comment.</summary>
    BlockComment,

    /// <summary>Inside a <c>{-# ... #-}</c> pragma.</summary>
    Pragma,

    /// <summary>Inside a string gap: a string broken over lines with <c>\</c> ... <c>\</c>.</summary>
    StringGap,
}

/// <summary>
/// Everything the lexer needs to carry from one line to the next. Two lines starting in equal
/// states always lex the same way, so an editor can stop re-lexing as soon as states line up again.
/// </summary>
/// <param name="Mode">The construct left open at the end of the previous line.</param>
/// <param name="CommentDepth">Nesting depth of block comments or pragmas; 0 in other modes.</param>
public readonly record struct LexState(LexMode Mode, int CommentDepth);
