using System;
using System.Collections.Generic;
using System.Linq;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>
/// Finds the blocks of a Daml file from its tokens and indentation: declarations, their layout
/// blocks (<c>where</c>, <c>with</c>, <c>do</c>, <c>of</c>, <c>let</c>, <c>\case</c>) and comments
/// over several lines. It follows the off-side rule loosely and never gives up: code that doesn't
/// parse still gets blocks, ending where the indentation says they do.
/// </summary>
public static class LayoutAnalyzer
{
    private static readonly string[] HeaderContinuations = ["(", ")", "[", "]", ",", "=>", "->", ":", "::", "=", "|"];

    /// <summary>The block tree of a file; the root is the <see cref="DamlBlockKind.Module"/> block.</summary>
    /// <param name="lines">Every line of the file, lexed with the state carried from line to line.</param>
    /// <param name="tabSize">How many columns a tab advances to, as the editor shows it.</param>
    public static DamlBlock Analyze(IReadOnlyList<LineTokens> lines, int tabSize = LayoutLine.DefaultTabSize)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }
        var root = new Builder(lines, tabSize).Run();
        return BlockComments.Place(root, lines);
    }

    // A block still being read. Declarations close at a line indented no further than they are;
    // layout blocks only at a line indented less than their body.
    private sealed class OpenBlock(DamlBlockKind kind, string? name, int startLine, int indent, bool isLayout)
    {
        public DamlBlockKind Kind { get; } = kind;
        public string? Name { get; } = name;
        public int StartLine { get; } = startLine;
        public bool IsLayout { get; } = isLayout;
        public int Indent { get; set; } = indent;
        public int HeaderEndLine { get; set; } = startLine;
        public int BodyIndent { get; set; } = -1;
        public int LastLine { get; set; } = startLine;
        public bool Pending { get; set; }
        public bool InHeader { get; set; }
        public int HeaderBrackets { get; set; }
        public List<DamlBlock> Children { get; set; } = new();

        public bool ClosesAt(int column) => IsLayout ? column < Indent : column <= Indent;

        public DamlBlock Close() =>
            new(Kind, Name, StartLine, LastLine, Math.Min(HeaderEndLine, LastLine), BodyIndent, Children);
    }

    private sealed class Builder(IReadOnlyList<LineTokens> lines, int tabSize)
    {
        private readonly List<OpenBlock> _stack = new();
        private OpenBlock Root => _stack[0];
        private OpenBlock Top => _stack[_stack.Count - 1];
        private bool _inModuleHeader;
        private int _moduleColumn;

        public DamlBlock Run()
        {
            _stack.Add(new OpenBlock(DamlBlockKind.Module, null, 0, -1, isLayout: true) { BodyIndent = 0 });
            var number = 0;
            foreach (var tokens in lines)
            {
                var line = new LayoutLine(tokens, tabSize);
                if (line.Count > 0)
                {
                    Read(line, number);
                }
                number++;
            }
            while (_stack.Count > 1)
            {
                Pop();
            }
            var root = Root.Close();
            var lastLine = LastLineWithText();
            return root with { EndLine = lastLine, HeaderEndLine = Math.Min(root.HeaderEndLine, lastLine) };
        }

        private void Read(LayoutLine line, int number)
        {
            var column = line.Column(0);
            if (ReadModuleHeader(line, number, column))
            {
                return;
            }
            var continuesHeader = ContinuesHeader(line, column);
            if (continuesHeader)
            {
                Top.HeaderEndLine = number;
            }
            else
            {
                CloseBlocksAt(column);
                NoteBody(number, column);
                StartDeclaration(line, number, column);
            }
            ReadTokens(line, number, startAt: 0);
            foreach (var block in _stack)
            {
                block.LastLine = number;
            }
        }

        // `module M (exports) where` heads the file; its `where` opens no block.
        private bool ReadModuleHeader(LayoutLine line, int number, int column)
        {
            if (!_inModuleHeader && _stack.Count == 1 && Root.Name is null && Root.Children.Count == 0 && line.Is(0, "module"))
            {
                _inModuleHeader = true;
                _moduleColumn = column;
                _stack[0] = new OpenBlock(DamlBlockKind.Module, line.Count > 1 ? line.Text(1) : null, 0, -1, isLayout: true) { BodyIndent = 0 };
            }
            else if (!_inModuleHeader || column <= _moduleColumn)
            {
                // A header without `where` ends at the next line that isn't indented under it.
                _inModuleHeader = false;
                return false;
            }
            Root.HeaderEndLine = number;
            Root.LastLine = number;
            for (var i = 0; i < line.Count; i++)
            {
                if (line.Is(i, "where"))
                {
                    _inModuleHeader = false;
                    break;
                }
            }
            return true;
        }

        // Lines indented under a declaration's first line carry its header on while brackets
        // are open or they start with something that can only continue it.
        private bool ContinuesHeader(LayoutLine line, int column)
        {
            if (!Top.InHeader)
            {
                return false;
            }
            if (column > Top.Indent && (Top.HeaderBrackets > 0 || line.IsAnyOf(0, HeaderContinuations)))
            {
                return true;
            }
            Top.InHeader = false;
            return false;
        }

        private void CloseBlocksAt(int column)
        {
            while (_stack.Count > 1)
            {
                var top = Top;
                if (top.Pending)
                {
                    // The first line after an opener sets the block's indent, if it's indented
                    // further than what it is in; otherwise the block is empty.
                    var outer = _stack[_stack.Count - 2];
                    if (column > outer.Indent)
                    {
                        top.Pending = false;
                        top.Indent = column;
                        top.BodyIndent = column;
                        return;
                    }
                    Pop();
                    continue;
                }
                if (!top.ClosesAt(column))
                {
                    return;
                }
                Pop();
            }
        }

        // A declaration's body indent is that of its first line past the header.
        private void NoteBody(int number, int column)
        {
            foreach (var block in _stack)
            {
                if (!block.IsLayout && block.BodyIndent < 0 && number > block.HeaderEndLine && column > block.Indent)
                {
                    block.BodyIndent = column;
                }
            }
        }

        private void StartDeclaration(LayoutLine line, int number, int column)
        {
            (DamlBlockKind Kind, string? Name)? declaration;
            if (_stack.Count == 1)
            {
                declaration = DeclarationHeader.TopLevel(line);
            }
            else if (Top.Kind == DamlBlockKind.WhereBlock && column == Top.Indent && _stack[_stack.Count - 2].Kind is DamlBlockKind.Template or DamlBlockKind.Interface)
            {
                declaration = DeclarationHeader.InContract(line);
            }
            else
            {
                declaration = null;
            }
            if (declaration is not { } found)
            {
                return;
            }
            if (Reopen(found.Kind, found.Name, column))
            {
                return;
            }
            _stack.Add(new OpenBlock(found.Kind, found.Name, number, column, isLayout: false) { InHeader = true });
        }

        // More imports join the group before them, and more equations the binding they belong to.
        private bool Reopen(DamlBlockKind kind, string? name, int column)
        {
            var siblings = Top.Children;
            if (siblings.Count == 0)
            {
                return false;
            }
            var previous = siblings[siblings.Count - 1];
            var joins = (kind == DamlBlockKind.ImportGroup && previous.Kind == DamlBlockKind.ImportGroup)
                || (kind == DamlBlockKind.TopLevelBinding && previous.Kind == DamlBlockKind.TopLevelBinding && name is not null && name == previous.Name);
            if (!joins)
            {
                return false;
            }
            siblings.RemoveAt(siblings.Count - 1);
            // The first equation after a signature (or a continued equation head) is still the
            // binding's header; later equations are its body.
            _stack.Add(new OpenBlock(previous.Kind, previous.Name, previous.StartLine, column, isLayout: false)
            {
                HeaderEndLine = previous.HeaderEndLine,
                BodyIndent = previous.BodyIndent,
                LastLine = previous.EndLine,
                Children = previous.Children.ToList(),
                InHeader = previous.HeaderEndLine == previous.StartLine,
            });
            return true;
        }

        private void ReadTokens(LayoutLine line, int number, int startAt)
        {
            for (var i = startAt; i < line.Count; i++)
            {
                if (Top.InHeader)
                {
                    CountBracket(line, i);
                    // What follows `=` is the body, however it's laid out.
                    if (line[i].Kind == DamlTokenKind.ReservedOp && line.Is(i, "=") && Top.HeaderBrackets == 0)
                    {
                        Top.InHeader = false;
                        Top.HeaderEndLine = number;
                    }
                }
                if (OpenerAt(line, i) is not { } kind)
                {
                    continue;
                }
                if (Top.InHeader)
                {
                    Top.InHeader = false;
                    Top.HeaderEndLine = number;
                }
                var block = new OpenBlock(kind, null, number, -1, isLayout: true);
                if (i + 1 < line.Count)
                {
                    block.Indent = line.Column(i + 1);
                    block.BodyIndent = block.Indent;
                }
                else
                {
                    block.Pending = true;
                }
                _stack.Add(block);
            }
        }

        private void CountBracket(LayoutLine line, int index)
        {
            if (line[index].Kind != DamlTokenKind.Punctuation)
            {
                return;
            }
            if (line.Is(index, "(") || line.Is(index, "[") || line.Is(index, "{"))
            {
                Top.HeaderBrackets++;
            }
            else if ((line.Is(index, ")") || line.Is(index, "]") || line.Is(index, "}")) && Top.HeaderBrackets > 0)
            {
                Top.HeaderBrackets--;
            }
        }

        // Compared in place by length first: this runs for every code token of the file.
        private static DamlBlockKind? OpenerAt(LayoutLine line, int index)
        {
            var token = line[index];
            if (token.Kind is DamlTokenKind.String or DamlTokenKind.Char)
            {
                return null;
            }
            return token.Length switch
            {
                2 when line.Is(index, "do") => DamlBlockKind.DoBlock,
                2 when line.Is(index, "of") => DamlBlockKind.CaseBlock,
                3 when line.Is(index, "let") => DamlBlockKind.LetBlock,
                4 when line.Is(index, "with") => DamlBlockKind.WithBlock,
                4 when line.Is(index, "case") && index > 0 && line.Is(index - 1, "\\") => DamlBlockKind.CaseBlock,
                5 when line.Is(index, "where") => DamlBlockKind.WhereBlock,
                _ => null,
            };
        }

        private void Pop()
        {
            var block = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            Top.Children.Add(block.Close());
        }

        private int LastLineWithText()
        {
            for (var number = lines.Count - 1; number > 0; number--)
            {
                if (lines[number].Tokens.Any(t => t.Kind != DamlTokenKind.Whitespace))
                {
                    return number;
                }
            }
            return 0;
        }
    }
}
