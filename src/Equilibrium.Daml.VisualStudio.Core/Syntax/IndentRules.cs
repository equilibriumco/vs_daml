using System;
using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>Where a new line should start, for smart indent after Enter.</summary>
public static class IndentRules
{
    // Tokens that, at the end of a line, leave what follows them for the next line.
    private static readonly string[] OpensNextLine = ["where", "with", "do", "of", "let", "in", "=", "<-", "->", "then", "else", "$"];

    /// <summary>
    /// The column a new line should start at, or null to leave it to the editor (which keeps
    /// the previous line's indent).
    /// </summary>
    /// <param name="lines">Every line of the file, the new line included.</param>
    /// <param name="lineIndex">The new line.</param>
    /// <param name="indentSize">How far one level of indentation goes.</param>
    /// <param name="tabSize">How many columns a tab advances to, as the editor shows it.</param>
    public static int? GetDesiredIndentation(IReadOnlyList<LineTokens> lines, int lineIndex, int indentSize, int tabSize = LayoutLine.DefaultTabSize)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }
        if (lineIndex <= 0 || lineIndex > lines.Count)
        {
            return 0;
        }

        // Inside a block comment the text lines up with the comment's previous line.
        var above = lines[lineIndex - 1];
        if (above.EndState.Mode == LexMode.BlockComment)
        {
            var startsComment = lineIndex < 2 || lines[lineIndex - 2].EndState.Mode != LexMode.BlockComment;
            return Indentation(above.Text, tabSize) + (startsComment ? "{- ".Length : 0);
        }

        var previous = PreviousCodeLine(lines, lineIndex);
        if (previous < 0)
        {
            return 0;
        }
        var line = new LayoutLine(lines[previous], tabSize);
        var indent = line.Column(0);
        var root = LayoutAnalyzer.Analyze(lines, tabSize);

        if (EndsTheModuleHeader(root, previous))
        {
            return 0;
        }
        if (OpensNext(line) || LeavesBracketOpen(line) || StartsDeclarationBody(root, previous, line))
        {
            return indent + indentSize;
        }
        if (ContinuesAHeader(root, previous))
        {
            return indent; // the next guard or signature line goes where this one is
        }
        return BodyIndentAround(root, previous, indent);
    }

    // A line inside a declaration's header but not its first: a guard, or a signature's `->` line.
    private static bool ContinuesAHeader(DamlBlock block, int line)
    {
        foreach (var child in block.Children)
        {
            if (child.StartLine < line && line <= child.HeaderEndLine)
            {
                return true;
            }
            if (child.StartLine <= line && line <= child.EndLine && ContinuesAHeader(child, line))
            {
                return true;
            }
        }
        return false;
    }

    private static int PreviousCodeLine(IReadOnlyList<LineTokens> lines, int lineIndex)
    {
        for (var number = lineIndex - 1; number >= 0; number--)
        {
            if (new LayoutLine(lines[number]).Count > 0)
            {
                return number;
            }
        }
        return -1;
    }

    private static bool EndsTheModuleHeader(DamlBlock root, int line) =>
        root.Name is not null && root.HeaderEndLine == line;

    private static bool OpensNext(LayoutLine line)
    {
        var last = line.Count - 1;
        if (line[last].Kind is DamlTokenKind.String or DamlTokenKind.Char)
        {
            return false;
        }
        foreach (var text in OpensNextLine)
        {
            if (line.Is(last, text))
            {
                return true;
            }
        }
        return line.Is(last, "case") && last > 0 && line.Is(last - 1, "\\");
    }

    private static bool LeavesBracketOpen(LayoutLine line)
    {
        var depth = 0;
        for (var i = 0; i < line.Count; i++)
        {
            if (line[i].Kind != DamlTokenKind.Punctuation)
            {
                continue;
            }
            if (line.Is(i, "(") || line.Is(i, "[") || line.Is(i, "{"))
            {
                depth++;
            }
            else if ((line.Is(i, ")") || line.Is(i, "]") || line.Is(i, "}")) && depth > 0)
            {
                depth--;
            }
        }
        return depth > 0;
    }

    // `template Asset`, `choice Give : ...` and the like are followed by their body; so is an
    // equation head that has no `=` or signature yet (`f x` before its guards or `= ...`).
    private static bool StartsDeclarationBody(DamlBlock block, int line, LayoutLine tokens)
    {
        foreach (var child in block.Children)
        {
            if (child.StartLine == line && child.Kind is DamlBlockKind.Template or DamlBlockKind.Interface or DamlBlockKind.Exception
                or DamlBlockKind.Choice or DamlBlockKind.InterfaceInstance)
            {
                return true;
            }
            if (child.StartLine == line && child.Kind == DamlBlockKind.TopLevelBinding && !tokens.Has("=") && !tokens.Has(":") && !tokens.Has("::"))
            {
                return true;
            }
            if (child.StartLine <= line && line <= child.EndLine && StartsDeclarationBody(child, line, tokens))
            {
                return true;
            }
        }
        return false;
    }

    // The body column of the innermost block the line belongs to: the next field, statement or
    // alternative goes there. A `do`, `case` or `let` whose body starts on the line itself sets
    // the column; a record's `with` on one line doesn't.
    private static int? BodyIndentAround(DamlBlock root, int line, int indent)
    {
        int? found = null;
        Visit(root);
        return found;

        void Visit(DamlBlock block)
        {
            if (block.StartLine > line || line > block.EndLine)
            {
                return;
            }
            if (block.BodyIndent >= 0)
            {
                var opensOnThisLine = block.StartLine == line && block.Kind is DamlBlockKind.DoBlock or DamlBlockKind.CaseBlock or DamlBlockKind.LetBlock;
                var inBody = line > block.HeaderEndLine && block.BodyIndent <= indent;
                if (opensOnThisLine || inBody || block.Kind == DamlBlockKind.Module)
                {
                    found = block.BodyIndent;
                }
            }
            foreach (var child in block.Children)
            {
                Visit(child);
            }
        }
    }

    private static int Indentation(string text, int tabSize)
    {
        var column = 0;
        foreach (var c in text)
        {
            if (c == ' ')
            {
                column++;
            }
            else if (c == '\t')
            {
                column = (column / tabSize + 1) * tabSize;
            }
            else
            {
                break;
            }
        }
        return column;
    }
}
