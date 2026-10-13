namespace Daml.VisualStudio.Core.Syntax;

/// <summary>Recognises the line a declaration starts on, and what it declares.</summary>
internal static class DeclarationHeader
{
    /// <summary>A declaration at the top of the module; anything else there counts as a binding.</summary>
    public static (DamlBlockKind Kind, string? Name) TopLevel(LayoutLine line)
    {
        if (line.Is(0, "import"))
        {
            return (DamlBlockKind.ImportGroup, null);
        }
        if (line.Is(0, "interface") && line.Is(1, "instance"))
        {
            return (DamlBlockKind.InterfaceInstance, line.TextBetween(1, IndexOf(line, "where")));
        }
        if (line.Is(0, "template"))
        {
            return (DamlBlockKind.Template, ConIdAfter(line, 0));
        }
        if (line.Is(0, "interface"))
        {
            return (DamlBlockKind.Interface, ConIdAfter(line, 0));
        }
        if (line.Is(0, "exception"))
        {
            return (DamlBlockKind.Exception, ConIdAfter(line, 0));
        }
        if (line.Is(0, "data") || line.Is(0, "newtype") || line.Is(0, "type"))
        {
            return (DamlBlockKind.Data, ConIdAfter(line, 0));
        }
        if (line.Is(0, "class"))
        {
            // A class with a context names itself after the `=>`.
            var arrow = IndexOf(line, "=>");
            return (DamlBlockKind.Class, ConIdAfter(line, arrow < line.Count ? arrow : 0));
        }
        if (line.Is(0, "instance"))
        {
            return (DamlBlockKind.Instance, line.TextBetween(0, IndexOf(line, "where")));
        }
        return (DamlBlockKind.TopLevelBinding, BindingName(line));
    }

    /// <summary>A choice or interface instance inside a template or interface; null for anything else.</summary>
    public static (DamlBlockKind Kind, string? Name)? InContract(LayoutLine line)
    {
        if (line.Is(0, "choice"))
        {
            return (DamlBlockKind.Choice, ConIdAfter(line, 0));
        }
        if (line.Count > 1 && line[0].Kind == DamlTokenKind.ChoiceModifier && line.Is(1, "choice"))
        {
            return (DamlBlockKind.Choice, ConIdAfter(line, 1));
        }
        if (line.Is(0, "interface") && line.Is(1, "instance"))
        {
            return (DamlBlockKind.InterfaceInstance, line.TextBetween(1, IndexOf(line, "where")));
        }
        return null;
    }

    // The name a binding defines: `f` in `f x = ...` and `f : ...`, `<+>` in `a <+> b = ...` and `(<+>) : ...`.
    private static string? BindingName(LayoutLine line)
    {
        if (line[0].Kind == DamlTokenKind.VarId)
        {
            if (line.Count > 1 && line[1].Kind is DamlTokenKind.Operator or DamlTokenKind.Backtick)
            {
                return line.Text(1).Trim('`');
            }
            return line.Text(0);
        }
        if (line.Count > 2 && line.Is(0, "(") && line[1].Kind == DamlTokenKind.Operator && line.Is(2, ")"))
        {
            return line.Text(1);
        }
        return null;
    }

    private static string? ConIdAfter(LayoutLine line, int index)
    {
        for (var i = index + 1; i < line.Count; i++)
        {
            if (line[i].Kind == DamlTokenKind.ConId)
            {
                return line.Text(i);
            }
        }
        return null;
    }

    // The index of the first token with this text, or the token count when there's none.
    private static int IndexOf(LayoutLine line, string text)
    {
        for (var i = 0; i < line.Count; i++)
        {
            if (line.Is(i, text))
            {
                return i;
            }
        }
        return line.Count;
    }
}
