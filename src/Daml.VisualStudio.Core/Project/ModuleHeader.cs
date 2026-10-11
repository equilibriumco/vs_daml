using System.Text.RegularExpressions;

namespace Daml.VisualStudio.Core.Project;

/// <summary>Reads the module name a Daml file declares in its <c>module X.Y where</c> header.</summary>
internal static class ModuleHeader
{
    private static readonly Regex Declaration = new(@"\Gmodule\s+([A-Z][A-Za-z0-9_']*(?:\.[A-Z][A-Za-z0-9_']*)*)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The declared module, or <c>null</c> when the file doesn't start with a module header once
    /// whitespace, comments and pragmas are skipped.
    /// </summary>
    public static string? DeclaredModule(string text)
    {
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (At(text, i, "{-"))
            {
                // Block comments nest; pragmas ({-# ... #-}) are block comments too.
                var depth = 0;
                do
                {
                    if (At(text, i, "{-"))
                    {
                        depth++;
                        i += 2;
                    }
                    else if (At(text, i, "-}"))
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                while (depth > 0 && i < text.Length);
            }
            else if (At(text, i, "--"))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else
            {
                var match = Declaration.Match(text, i);
                return match.Success ? match.Groups[1].Value : null;
            }
        }
        return null;
    }

    private static bool At(string text, int index, string token) =>
        string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
