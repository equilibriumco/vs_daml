using System.Collections.Generic;
using System.Linq;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>Adds <c>{- ... -}</c> comments that run over several lines to a block tree.</summary>
internal static class BlockComments
{
    /// <summary>
    /// The tree with each comment placed in the innermost block that holds all of it. A comment
    /// that starts in one block and ends in another belongs to neither and is left out.
    /// </summary>
    public static DamlBlock Place(DamlBlock root, IReadOnlyList<LineTokens> lines)
    {
        foreach (var (first, last) in Find(lines))
        {
            if (first >= root.StartLine && last <= root.EndLine)
            {
                root = Insert(root, new DamlBlock(DamlBlockKind.BlockComment, null, first, last, first, -1, []));
            }
        }
        return root;
    }

    // Runs of lines that end inside a comment (or a pragma, which folds the same way), plus the
    // line that closes it. A line that closes one comment and opens another, with code between,
    // ends the first run and starts the next.
    private static IEnumerable<(int First, int Last)> Find(IReadOnlyList<LineTokens> lines)
    {
        var first = -1;
        var number = 0;
        foreach (var current in lines)
        {
            var inside = IsInside(current.EndState.Mode);
            if (inside && first >= 0 && HasCodeBetweenComments(current))
            {
                yield return (first, number);
                first = number;
            }
            else if (inside && first < 0)
            {
                first = number;
            }
            else if (!inside && first >= 0)
            {
                yield return (first, number);
                first = -1;
            }
            number++;
        }
        if (first >= 0)
        {
            // Never closed: it runs to the last line with anything on it.
            var last = lines.Count - 1;
            while (last > first && lines[last].Tokens.All(t => t.Kind == DamlTokenKind.Whitespace))
            {
                last--;
            }
            if (last > first)
            {
                yield return (first, last);
            }
        }
    }

    private static bool IsInside(LexMode mode) => mode is LexMode.BlockComment or LexMode.Pragma;

    private static bool HasCodeBetweenComments(LineTokens line) =>
        line.Tokens.Any(t => t.Kind is not (DamlTokenKind.Whitespace or DamlTokenKind.BlockComment or DamlTokenKind.Pragma or DamlTokenKind.LineComment or DamlTokenKind.DocComment));

    private static DamlBlock Insert(DamlBlock block, DamlBlock comment)
    {
        var children = block.Children.ToList();
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.StartLine <= comment.StartLine && comment.EndLine <= child.EndLine)
            {
                children[i] = Insert(child, comment);
                return block with { Children = children };
            }
            if (child.StartLine <= comment.EndLine && comment.StartLine <= child.EndLine)
            {
                return block; // half inside another block: not a block of its own
            }
        }
        var at = children.FindIndex(c => c.StartLine > comment.StartLine);
        children.Insert(at < 0 ? children.Count : at, comment);
        return block with { Children = children };
    }
}
