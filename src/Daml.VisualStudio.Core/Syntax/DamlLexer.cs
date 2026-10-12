using System;
using System.Collections.Generic;
using System.Globalization;

namespace Daml.VisualStudio.Core.Syntax;

/// <summary>
/// Line-at-a-time lexer for Daml (Haskell 2010 lexical syntax plus Daml's keywords). Each line is
/// lexed on its own, given the state the previous line ended in, so an editor only re-lexes the
/// lines an edit can affect.
/// </summary>
public static class DamlLexer
{
    private const string AsciiSymbols = "!#$%&*+./<=>?@\\^|-~:";

    private static readonly (string Word, DamlTokenKind Kind)[] Keywords =
    [
        ("module", DamlTokenKind.ModuleKeyword),
        ("import", DamlTokenKind.ImportKeyword),
        ("qualified", DamlTokenKind.ImportKeyword),
        ("as", DamlTokenKind.ImportKeyword),
        ("hiding", DamlTokenKind.ImportKeyword),
        ("where", DamlTokenKind.Keyword),
        ("forall", DamlTokenKind.Keyword),
        ("infix", DamlTokenKind.Keyword),
        ("infixl", DamlTokenKind.Keyword),
        ("infixr", DamlTokenKind.Keyword),
        ("template", DamlTokenKind.DeclarationKeyword),
        ("interface", DamlTokenKind.DeclarationKeyword),
        ("exception", DamlTokenKind.DeclarationKeyword),
        ("data", DamlTokenKind.DeclarationKeyword),
        ("type", DamlTokenKind.DeclarationKeyword),
        ("newtype", DamlTokenKind.DeclarationKeyword),
        ("class", DamlTokenKind.DeclarationKeyword),
        ("instance", DamlTokenKind.DeclarationKeyword),
        ("deriving", DamlTokenKind.DeclarationKeyword),
        ("let", DamlTokenKind.DeclarationKeyword),
        ("in", DamlTokenKind.DeclarationKeyword),
        ("with", DamlTokenKind.ContractKeyword),
        ("choice", DamlTokenKind.ContractKeyword),
        ("controller", DamlTokenKind.ContractKeyword),
        ("signatory", DamlTokenKind.ContractKeyword),
        ("observer", DamlTokenKind.ContractKeyword),
        ("ensure", DamlTokenKind.ContractKeyword),
        // Contract keys, exceptions and agreements are gone or changed in Daml 3, but older code
        // still uses them, so they keep their colour.
        ("key", DamlTokenKind.ContractKeyword),
        ("maintainer", DamlTokenKind.ContractKeyword),
        ("agreement", DamlTokenKind.ContractKeyword),
        ("message", DamlTokenKind.ContractKeyword),
        ("viewtype", DamlTokenKind.ContractKeyword),
        ("requires", DamlTokenKind.ContractKeyword),
        ("implements", DamlTokenKind.ContractKeyword),
        ("for", DamlTokenKind.ContractKeyword),
        ("nonconsuming", DamlTokenKind.ChoiceModifier),
        ("preconsuming", DamlTokenKind.ChoiceModifier),
        ("postconsuming", DamlTokenKind.ChoiceModifier),
        ("do", DamlTokenKind.ControlKeyword),
        ("if", DamlTokenKind.ControlKeyword),
        ("then", DamlTokenKind.ControlKeyword),
        ("else", DamlTokenKind.ControlKeyword),
        ("case", DamlTokenKind.ControlKeyword),
        ("of", DamlTokenKind.ControlKeyword),
        ("try", DamlTokenKind.ControlKeyword),
        ("catch", DamlTokenKind.ControlKeyword),
    ];

    private static readonly string[] ReservedOperators = ["..", ":", "::", "=", "\\", "|", "<-", "->", "@", "~", "=>"];

    /// <summary>Lexes one line, appending its tokens to <paramref name="output"/>.</summary>
    /// <param name="line">The line's text, without the line break.</param>
    /// <param name="state">The state the previous line ended in; <c>default</c> for the first line.</param>
    /// <param name="output">Receives the tokens, which cover the line exactly, in order.</param>
    /// <returns>The state this line ends in, to pass along with the next line.</returns>
    public static LexState LexLine(ReadOnlySpan<char> line, LexState state, IList<DamlToken> output)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        var i = 0;
        switch (state.Mode)
        {
            case LexMode.BlockComment:
            case LexMode.Pragma:
            {
                var depth = Math.Max(1, state.CommentDepth);
                var end = SkipNestedComment(line, 0, ref depth);
                Add(output, 0, end, state.Mode == LexMode.Pragma ? DamlTokenKind.Pragma : DamlTokenKind.BlockComment);
                if (depth > 0)
                {
                    return new LexState(state.Mode, depth);
                }
                i = end;
                break;
            }
            case LexMode.StringGap:
            {
                var indent = SkipWhitespace(line, 0);
                if (indent == line.Length)
                {
                    Add(output, 0, indent, DamlTokenKind.String);
                    return state;
                }
                if (line[indent] == '\\')
                {
                    var (end, gapOpen) = ScanStringBody(line, indent + 1);
                    Add(output, 0, end, DamlTokenKind.String);
                    if (gapOpen)
                    {
                        return new LexState(LexMode.StringGap, 0);
                    }
                    i = end;
                }

                // Anything else means the gap was never closed; carry on as code rather than
                // colouring the rest of the file as a string.
                break;
            }
        }

        while (i < line.Length)
        {
            var c = line[i];
            if (char.IsWhiteSpace(c))
            {
                var end = SkipWhitespace(line, i);
                Add(output, i, end - i, DamlTokenKind.Whitespace);
                i = end;
            }
            else if (c == '{' && At(line, i + 1, '-'))
            {
                var pragma = At(line, i + 2, '#');
                var depth = 0;
                var end = SkipNestedComment(line, i, ref depth);
                Add(output, i, end - i, pragma ? DamlTokenKind.Pragma : DamlTokenKind.BlockComment);
                if (depth > 0)
                {
                    return new LexState(pragma ? LexMode.Pragma : LexMode.BlockComment, depth);
                }
                i = end;
            }
            else if (c == '"')
            {
                var (end, gapOpen) = ScanStringBody(line, i + 1);
                Add(output, i, end - i, DamlTokenKind.String);
                if (gapOpen)
                {
                    return new LexState(LexMode.StringGap, 0);
                }
                i = end;
            }
            else if (c == '\'')
            {
                var length = CharLiteralLength(line, i);
                Add(output, i, Math.Max(1, length), length > 0 ? DamlTokenKind.Char : DamlTokenKind.Punctuation);
                i += Math.Max(1, length);
            }
            else if (c is >= '0' and <= '9')
            {
                var end = ScanNumber(line, i);
                Add(output, i, end - i, DamlTokenKind.Number);
                i = end;
            }
            else if (IsNameStart(c))
            {
                var (end, kind) = ScanName(line, i);
                Add(output, i, end - i, kind);
                i = end;
            }
            else if (c == '`')
            {
                var end = i + 1;
                if (end < line.Length && IsNameStart(line[end]))
                {
                    var (nameEnd, _) = ScanName(line, end);
                    if (At(line, nameEnd, '`'))
                    {
                        end = nameEnd + 1;
                    }
                }
                Add(output, i, end - i, DamlTokenKind.Backtick);
                i = end;
            }
            else if (c == '(' && At(line, i + 1, ')'))
            {
                Add(output, i, 2, DamlTokenKind.Unit);
                i += 2;
            }
            else if (c is '(' or ')' or '[' or ']' or ',' or ';' or '{' or '}')
            {
                Add(output, i, 1, DamlTokenKind.Punctuation);
                i++;
            }
            else if (IsSymbol(c))
            {
                var end = i;
                while (end < line.Length && IsSymbol(line[end]))
                {
                    end++;
                }
                var run = line.Slice(i, end - i);
                if (run.Length >= 2 && IsAllDashes(run))
                {
                    // A run of dashes on its own starts a comment; with any other symbol in it
                    // (-->, |--) it's an operator.
                    Add(output, i, line.Length - i, IsDocComment(line, end) ? DamlTokenKind.DocComment : DamlTokenKind.LineComment);
                    return default;
                }
                Add(output, i, end - i, ClassifyOperator(line, i, run));
                i = end;
            }
            else
            {
                var length = i + 1 < line.Length && char.IsSurrogatePair(c, line[i + 1]) ? 2 : 1;
                Add(output, i, length, DamlTokenKind.Error);
                i += length;
            }
        }
        return default;
    }

    private static void Add(IList<DamlToken> output, int start, int length, DamlTokenKind kind)
    {
        if (length > 0)
        {
            output.Add(new DamlToken(start, length, kind));
        }
    }

    private static bool At(ReadOnlySpan<char> line, int index, char expected) => index < line.Length && line[index] == expected;

    private static int SkipWhitespace(ReadOnlySpan<char> line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        return index;
    }

    // Walks a nested comment from index, adjusting depth; returns where it closed (depth 0) or the line end.
    private static int SkipNestedComment(ReadOnlySpan<char> line, int index, ref int depth)
    {
        while (index < line.Length)
        {
            if (line[index] == '{' && At(line, index + 1, '-'))
            {
                depth++;
                index += 2;
            }
            else if (line[index] == '-' && At(line, index + 1, '}'))
            {
                index += 2;
                if (--depth == 0)
                {
                    return index;
                }
            }
            else
            {
                index++;
            }
        }
        return line.Length;
    }

    // From just after an opening quote (or a gap's closing backslash) to just after the closing
    // quote. A backslash followed by whitespace opens a gap; if the line ends before the gap's
    // closing backslash, the string continues on the next line. A string left open without a gap
    // stops at the line end, which is what a half-typed string looks like.
    private static (int End, bool GapOpen) ScanStringBody(ReadOnlySpan<char> line, int index)
    {
        while (index < line.Length)
        {
            var c = line[index];
            if (c == '"')
            {
                return (index + 1, false);
            }
            if (c != '\\')
            {
                index++;
                continue;
            }
            if (index + 1 == line.Length)
            {
                return (line.Length, true);
            }
            if (char.IsWhiteSpace(line[index + 1]))
            {
                var gapEnd = SkipWhitespace(line, index + 1);
                if (gapEnd == line.Length)
                {
                    return (line.Length, true);
                }
                index = line[gapEnd] == '\\' ? gapEnd + 1 : gapEnd;
                continue;
            }
            index += 2; // an escape: whatever follows the backslash can't end the string
        }
        return (line.Length, false);
    }

    // Length of a character literal starting at index, or 0 when the apostrophe is a promotion
    // tick ('[], 'True) or a Template Haskell quote.
    private static int CharLiteralLength(ReadOnlySpan<char> line, int index)
    {
        if (At(line, index + 1, '\\'))
        {
            // '\n', '\'', '\\', '\x41', '\1234'
            var end = index + 3;
            while (end < line.Length && end - index <= 12 && line[end] != '\'' && !char.IsWhiteSpace(line[end]))
            {
                end++;
            }
            return At(line, end, '\'') && end > index + 2 ? end + 1 - index : 0;
        }
        return index + 2 < line.Length && line[index + 1] != '\'' && line[index + 2] == '\'' ? 3 : 0;
    }

    private static int ScanNumber(ReadOnlySpan<char> line, int index)
    {
        if (line[index] == '0' && index + 2 < line.Length)
        {
            var radix = char.ToLowerInvariant(line[index + 1]) switch
            {
                'x' => 16,
                'o' => 8,
                'b' => 2,
                _ => 0,
            };
            if (radix != 0 && IsDigit(line[index + 2], radix))
            {
                return ScanDigits(line, index + 2, radix);
            }
        }

        var end = ScanDigits(line, index, 10);
        if (At(line, end, '.') && end + 1 < line.Length && IsDigit(line[end + 1], 10))
        {
            end = ScanDigits(line, end + 1, 10);
        }
        if (end < line.Length && (line[end] == 'e' || line[end] == 'E'))
        {
            var exponent = end + 1;
            if (exponent < line.Length && (line[exponent] == '+' || line[exponent] == '-'))
            {
                exponent++;
            }
            if (exponent < line.Length && IsDigit(line[exponent], 10))
            {
                end = ScanDigits(line, exponent, 10);
            }
        }
        return end;
    }

    // Digits with NumericUnderscores: underscores count only when a digit follows them.
    private static int ScanDigits(ReadOnlySpan<char> line, int index, int radix)
    {
        while (index < line.Length)
        {
            if (IsDigit(line[index], radix))
            {
                index++;
                continue;
            }
            var next = index;
            while (next < line.Length && line[next] == '_')
            {
                next++;
            }
            if (next == index || next == line.Length || !IsDigit(line[next], radix))
            {
                break;
            }
            index = next;
        }
        return index;
    }

    private static bool IsDigit(char c, int radix) => radix switch
    {
        16 => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'),
        8 => c is >= '0' and <= '7',
        2 => c is '0' or '1',
        _ => c is >= '0' and <= '9',
    };

    internal static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

    internal static bool IsNameChar(char c) => c == '_' || c == '\'' || char.IsLetterOrDigit(c);

    private static bool IsUpper(char c) => char.IsUpper(c) || char.GetUnicodeCategory(c) == UnicodeCategory.TitlecaseLetter;

    internal static bool IsSymbol(char c) =>
        c < 128 ? AsciiSymbols.IndexOf(c) >= 0 : char.GetUnicodeCategory(c) is UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;

    private static int ScanIdentifier(ReadOnlySpan<char> line, int index)
    {
        while (index < line.Length && IsNameChar(line[index]))
        {
            index++;
        }
        return index;
    }

    // A name, possibly qualified: Module.Sub.name, Module.Type, Module.+ . Only capitalised
    // segments can qualify, so this.owner is a variable, a dot and a field.
    private static (int End, DamlTokenKind Kind) ScanName(ReadOnlySpan<char> line, int index)
    {
        var end = ScanIdentifier(line, index);
        if (!IsUpper(line[index]))
        {
            return (end, KeywordKind(line.Slice(index, end - index)));
        }

        var qualified = false;
        while (At(line, end, '.') && end + 1 < line.Length)
        {
            var next = line[end + 1];
            if (IsNameStart(next))
            {
                var segmentEnd = ScanIdentifier(line, end + 1);
                var upper = IsUpper(next);
                end = segmentEnd;
                qualified = true;
                if (!upper)
                {
                    break;
                }
            }
            else if (IsSymbol(next))
            {
                end++;
                while (end < line.Length && IsSymbol(line[end]))
                {
                    end++;
                }
                qualified = true;
                break;
            }
            else
            {
                break;
            }
        }
        if (qualified)
        {
            return (end, DamlTokenKind.QualifiedName);
        }

        var word = line.Slice(index, end - index);
        return (end, word.SequenceEqual("True".AsSpan()) || word.SequenceEqual("False".AsSpan()) ? DamlTokenKind.Boolean : DamlTokenKind.ConId);
    }

    private static DamlTokenKind KeywordKind(ReadOnlySpan<char> word)
    {
        foreach (var (keyword, kind) in Keywords)
        {
            if (keyword.Length == word.Length && word.SequenceEqual(keyword.AsSpan()))
            {
                return kind;
            }
        }
        return DamlTokenKind.VarId;
    }

    private static bool IsAllDashes(ReadOnlySpan<char> run)
    {
        foreach (var c in run)
        {
            if (c != '-')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDocComment(ReadOnlySpan<char> line, int afterDashes)
    {
        var index = afterDashes;
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
        {
            index++;
        }
        return index < line.Length && (line[index] == '|' || line[index] == '^');
    }

    private static DamlTokenKind ClassifyOperator(ReadOnlySpan<char> line, int start, ReadOnlySpan<char> run)
    {
        if (run.Length == 1 && run[0] == '@')
        {
            // x@(...) is an as-pattern; f @Type is a type application.
            var afterName = start > 0 && (IsNameChar(line[start - 1]) || line[start - 1] == ')' || line[start - 1] == ']');
            var beforeType = start + 1 < line.Length && (IsNameStart(line[start + 1]) || line[start + 1] is '(' or '[' or '\'');
            return !afterName && beforeType ? DamlTokenKind.TypeApplication : DamlTokenKind.ReservedOp;
        }
        foreach (var reserved in ReservedOperators)
        {
            if (run.SequenceEqual(reserved.AsSpan()))
            {
                return DamlTokenKind.ReservedOp;
            }
        }
        return DamlTokenKind.Operator;
    }
}
