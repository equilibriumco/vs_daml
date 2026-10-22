using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Equilibrium.Daml.VisualStudio.Core.Snippets;

/// <summary>Where a field ended up in an expanded snippet.</summary>
/// <param name="Number">The field's number; 0 is the final caret stop.</param>
/// <param name="Start">Offset in the expanded text.</param>
/// <param name="Length">Length of the field's default text.</param>
public sealed record SnippetField(int Number, int Start, int Length);

/// <summary>A snippet as text ready to insert.</summary>
/// <param name="Text">The text with every field replaced by its default.</param>
/// <param name="Fields">Every field occurrence, in text order.</param>
/// <param name="Selection">What to select after inserting: the first field, else the final stop, else the end.</param>
public sealed record ExpandedSnippet(string Text, IReadOnlyList<SnippetField> Fields, SnippetField Selection);

/// <summary>Turns a snippet body into the text to insert.</summary>
public static class SnippetExpander
{
    /// <summary>Expands a snippet body.</summary>
    /// <param name="body">The body, as in <see cref="DamlSnippet.Body"/>.</param>
    /// <param name="indent">The indentation of the line it goes into; every line after the first gets it.</param>
    /// <param name="newline">The line break the document uses.</param>
    public static ExpandedSnippet Expand(string body, string indent, string newline)
    {
        if (body is null)
        {
            throw new ArgumentNullException(nameof(body));
        }
        var text = new StringBuilder();
        var fields = new List<SnippetField>();
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (c == '\n')
            {
                text.Append(newline).Append(indent);
                i++;
            }
            else if (c == '$' && TryField(body, i, out var number, out var defaultText, out var next))
            {
                fields.Add(new SnippetField(number, text.Length, defaultText.Length));
                text.Append(defaultText);
                i = next;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }
        var result = text.ToString();
        var selection = fields.Where(f => f.Number > 0).OrderBy(f => f.Number).ThenBy(f => f.Start).FirstOrDefault()
            ?? fields.FirstOrDefault(f => f.Number == 0)
            ?? new SnippetField(0, result.Length, 0);
        return new ExpandedSnippet(result, fields, selection);
    }

    // `$1`, `${1}` or `${1:default}` at `start`.
    private static bool TryField(string body, int start, out int number, out string defaultText, out int next)
    {
        number = 0;
        defaultText = "";
        next = start + 1;
        var braced = next < body.Length && body[next] == '{';
        var digits = braced ? next + 1 : next;
        var end = digits;
        while (end < body.Length && char.IsDigit(body[end]))
        {
            end++;
        }
        if (end == digits)
        {
            return false;
        }
        number = int.Parse(body.Substring(digits, end - digits), System.Globalization.CultureInfo.InvariantCulture);
        if (!braced)
        {
            next = end;
            return true;
        }
        if (end < body.Length && body[end] == '}')
        {
            next = end + 1;
            return true;
        }
        if (end < body.Length && body[end] == ':')
        {
            var close = body.IndexOf('}', end + 1);
            if (close < 0)
            {
                return false;
            }
            defaultText = body.Substring(end + 1, close - end - 1);
            next = close + 1;
            return true;
        }
        return false;
    }
}
