using System;
using System.Collections;
using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

// A persistent balanced sequence. Edits reuse untouched subtrees; old snapshots remain safe
// for background readers. Small leaves bound both copying and retention of replaced lines.
internal sealed class TokenLines : IReadOnlyList<LineTokens>
{
    private const int LeafSize = 32;
    private readonly Node? _root;

    private TokenLines(Node? root) => _root = root;

    public static TokenLines Empty { get; } = new(null);

    public int Count => _root?.Count ?? 0;

    public LineTokens this[int index] => index >= 0 && index < Count
        ? _root!.At(index)
        : throw new ArgumentOutOfRangeException(nameof(index));

    public static TokenLines From(IReadOnlyList<LineTokens> lines) => new(Build(lines, 0, lines.Count));

    public TokenLines Replace(int first, int removed, IReadOnlyList<LineTokens> added) =>
        new(Join(Join(Slice(_root, 0, first), Build(added, 0, added.Count)), Slice(_root, first + removed, Count - first - removed)));

    public IEnumerator<LineTokens> GetEnumerator() => Walk(_root).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static Node? Build(IReadOnlyList<LineTokens> lines, int start, int count)
    {
        if (count == 0)
        {
            return null;
        }
        if (count <= LeafSize)
        {
            var values = new LineTokens[count];
            for (var i = 0; i < count; i++)
            {
                values[i] = lines[start + i];
            }
            return new Leaf(values, 0, count);
        }
        var half = count / 2;
        return new Branch(Build(lines, start, half)!, Build(lines, start + half, count - half)!);
    }

    private static Node? Slice(Node? node, int start, int count)
    {
        if (count == 0 || node is null)
        {
            return null;
        }
        if (start == 0 && count == node.Count)
        {
            return node;
        }
        if (node is Leaf leaf)
        {
            return new Leaf(leaf.Values, leaf.Start + start, count);
        }
        var branch = (Branch)node;
        if (start >= branch.Left.Count)
        {
            return Slice(branch.Right, start - branch.Left.Count, count);
        }
        var leftCount = Math.Min(count, branch.Left.Count - start);
        return Join(Slice(branch.Left, start, leftCount), Slice(branch.Right, 0, count - leftCount));
    }

    private static Node? Join(Node? left, Node? right)
    {
        if (left is null) { return right; }
        if (right is null) { return left; }
        if (left.Height > right.Height + 1)
        {
            var branch = (Branch)left;
            return Balance(branch.Left, Join(branch.Right, right)!);
        }
        if (right.Height > left.Height + 1)
        {
            var branch = (Branch)right;
            return Balance(Join(left, branch.Left)!, branch.Right);
        }
        return new Branch(left, right);
    }

    private static Branch Balance(Node left, Node right)
    {
        if (left.Height > right.Height + 1)
        {
            var heavy = (Branch)left;
            if (heavy.Left.Height >= heavy.Right.Height)
            {
                return new Branch(heavy.Left, new Branch(heavy.Right, right));
            }
            var middle = (Branch)heavy.Right;
            return new Branch(new Branch(heavy.Left, middle.Left), new Branch(middle.Right, right));
        }
        if (right.Height > left.Height + 1)
        {
            var heavy = (Branch)right;
            if (heavy.Right.Height >= heavy.Left.Height)
            {
                return new Branch(new Branch(left, heavy.Left), heavy.Right);
            }
            var middle = (Branch)heavy.Left;
            return new Branch(new Branch(left, middle.Left), new Branch(middle.Right, heavy.Right));
        }
        return new Branch(left, right);
    }

    private static IEnumerable<LineTokens> Walk(Node? root)
    {
        var pending = new Stack<Node>();
        if (root is not null) { pending.Push(root); }
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is Leaf leaf)
            {
                for (var i = 0; i < leaf.Count; i++) { yield return leaf.Values[leaf.Start + i]; }
            }
            else
            {
                var branch = (Branch)node;
                pending.Push(branch.Right);
                pending.Push(branch.Left);
            }
        }
    }

    private abstract class Node(int count, int height)
    {
        public int Count { get; } = count;
        public int Height { get; } = height;
        public abstract LineTokens At(int index);
    }

    private sealed class Leaf(LineTokens[] values, int start, int count) : Node(count, 1)
    {
        public LineTokens[] Values { get; } = values;
        public int Start { get; } = start;
        public override LineTokens At(int index) => Values[Start + index];
    }

    private sealed class Branch(Node left, Node right) : Node(left.Count + right.Count, Math.Max(left.Height, right.Height) + 1)
    {
        public Node Left { get; } = left;
        public Node Right { get; } = right;
        public override LineTokens At(int index) => index < Left.Count ? Left.At(index) : Right.At(index - Left.Count);
    }
}
