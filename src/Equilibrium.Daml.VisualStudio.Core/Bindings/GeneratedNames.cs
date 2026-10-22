using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>The identifier encoding used by the pinned C# emitter.</summary>
public static class GeneratedNames
{
    /// <summary>Decodes damlc's LF identifier escaping to the source spelling.</summary>
    public static string FromLf(string name) => Decode(name, '$');

    /// <summary>Decodes an emitted C# identifier body to the source spelling.</summary>
    public static string FromCSharp(string name) => Decode(name.TrimStart('@'), '_');

    /// <summary>Maps an original LF module to its generated namespace, including prefix elision.</summary>
    public static string NamespaceOf(string module, string? prefix)
    {
        var encoded = string.Join(".", module.Split('.').Select(s => Encode(FromLf(s))));
        prefix = prefix is null ? null : BareNamespace(prefix);
        return prefix is null || encoded == prefix || encoded.StartsWith(prefix + ".", StringComparison.Ordinal)
            ? encoded : prefix + "." + encoded;
    }

    /// <summary>Removes C# keyword escapes from namespace segments.</summary>
    public static string BareNamespace(string value) => string.Join(".", value.Split('.').Select(s => s.TrimStart('@')));

    private static string Decode(string name, char escape)
    {
        var result = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == escape && i + 1 < name.Length && name[i + 1] == escape)
            {
                result.Append(escape);
                i++;
            }
            else if (name[i] == escape && i + 5 < name.Length && HexBody(name.Substring(i + 1, 5)))
            {
                result.Append((char)int.Parse(name.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 5;
            }
            else { result.Append(name[i]); }
        }
        return result.ToString();
    }

    private static string Encode(string name)
    {
        var result = "";
        for (var i = name.Length - 1; i >= 0; i--)
        {
            var c = name[i];
            var fragment = c == '_'
                ? (result.StartsWith("_", StringComparison.Ordinal) || HexBody(result) ? "__" : "_")
                : c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') || (i > 0 && c is >= '0' and <= '9')
                    ? c.ToString() : "_u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);
            result = fragment + result;
        }
        return result;
    }

    private static bool HexBody(string value) => value.Length >= 5 && value[0] == 'u'
        && value.Skip(1).Take(4).All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
