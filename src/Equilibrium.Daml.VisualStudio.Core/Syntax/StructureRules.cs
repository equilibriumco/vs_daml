using System;
using System.Collections.Generic;
using System.Linq;

namespace Equilibrium.Daml.VisualStudio.Core.Syntax;

/// <summary>How the editor treats a foldable region: as a type, a member, or neither.</summary>
public enum StructureKind
{
    /// <summary>Templates, interfaces, exceptions, data types, classes and instances.</summary>
    Type,

    /// <summary>Choices and top-level bindings.</summary>
    Member,

    /// <summary>Import groups, layout blocks and comments.</summary>
    Nonstructural,
}

/// <summary>A foldable region of a Daml file. Lines count from zero.</summary>
/// <param name="Kind">What the region is to the editor.</param>
/// <param name="StartLine">The line the region starts on.</param>
/// <param name="StartColumn">Where on that line folding starts, or <see cref="EndOfLine"/> to keep the line visible.</param>
/// <param name="EndLine">The region's last line; it folds to that line's end.</param>
/// <param name="HeaderEndLine">The last line of the region's header.</param>
/// <param name="CollapsedForm">The text shown in place of the folded region.</param>
public sealed record StructureSpec(StructureKind Kind, int StartLine, int StartColumn, int EndLine, int HeaderEndLine, string CollapsedForm)
{
    /// <summary>A <see cref="StartColumn"/> that folds from the end of the first line.</summary>
    public const int EndOfLine = -1;
}

/// <summary>Turns a block tree into the regions the editor can fold.</summary>
public static class StructureRules
{
    private const int MaxCollapsedLength = 80;

    /// <summary>
    /// The regions of every block over more than one line, in document order with outer regions
    /// first. <c>do</c> and <c>with</c> blocks need three lines; shorter ones aren't worth folding.
    /// </summary>
    /// <param name="root">The tree from <see cref="LayoutAnalyzer.Analyze"/>.</param>
    /// <param name="lines">The text of the lines the tree was built from.</param>
    public static IReadOnlyList<StructureSpec> FromBlocks(DamlBlock root, IReadOnlyList<string> lines)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }
        var result = new List<StructureSpec>();
        foreach (var child in root.Children)
        {
            Add(child, lines, result);
        }
        return result;
    }

    private static void Add(DamlBlock block, IReadOnlyList<string> lines, List<StructureSpec> result)
    {
        if (Folds(block) && block.EndLine < lines.Count)
        {
            result.Add(Spec(block, lines));
        }
        foreach (var child in block.Children)
        {
            Add(child, lines, result);
        }
    }

    private static bool Folds(DamlBlock block)
    {
        var length = block.EndLine - block.StartLine + 1;
        return block.Kind is DamlBlockKind.DoBlock or DamlBlockKind.WithBlock ? length >= 3 : length >= 2;
    }

    private static StructureSpec Spec(DamlBlock block, IReadOnlyList<string> lines)
    {
        var first = lines[block.StartLine];
        switch (block.Kind)
        {
            case DamlBlockKind.ImportGroup:
                var imports = Enumerable.Range(block.StartLine, block.EndLine - block.StartLine + 1).Count(n => lines[n].StartsWith("import", StringComparison.Ordinal));
                return new(StructureKind.Nonstructural, block.StartLine, Indent(first), block.EndLine, block.HeaderEndLine, $"import … ({imports})");
            case DamlBlockKind.Template or DamlBlockKind.Interface or DamlBlockKind.InterfaceInstance or DamlBlockKind.Exception
                or DamlBlockKind.Data or DamlBlockKind.Class or DamlBlockKind.Instance:
                return new(StructureKind.Type, block.StartLine, Indent(first), block.EndLine, block.HeaderEndLine, Declaration(first, block.Name));
            case DamlBlockKind.Choice or DamlBlockKind.TopLevelBinding:
                return new(StructureKind.Member, block.StartLine, Indent(first), block.EndLine, block.HeaderEndLine, Declaration(first, block.Name));
            default:
                return new(StructureKind.Nonstructural, block.StartLine, StructureSpec.EndOfLine, block.EndLine, block.HeaderEndLine, "…");
        }
    }

    // The first line up to the declared name: `template Asset …`, `nonconsuming choice Give …`.
    private static string Declaration(string line, string? name)
    {
        var text = line.Trim();
        var end = name is null ? -1 : text.IndexOf(name, StringComparison.Ordinal);
        if (end >= 0)
        {
            text = text.Substring(0, end + name!.Length);
        }
        if (text.Length > MaxCollapsedLength)
        {
            text = text.Substring(0, MaxCollapsedLength).TrimEnd();
        }
        return text + " …";
    }

    private static int Indent(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }
        return i;
    }
}
