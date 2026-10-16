using System;
using System.IO;
using Daml.Build.Core.FileSystem;

namespace Daml.VisualStudio.Core.Server;

/// <summary>
/// The address of a script's results, as damlc hands it out in a code lens:
/// <c>daml://compiler?file=&lt;escaped path&gt;&amp;top-level-decl=&lt;name&gt;</c>. Opening that
/// address as a document makes the server run the script and send its results.
/// Two addresses are the same resource when they name the same file (Windows paths, so case and
/// slash direction don't matter) and the same declaration (case matters).
/// </summary>
public sealed class VirtualResourceUri : IEquatable<VirtualResourceUri>
{
    private const string Prefix = "daml://compiler?";

    private VirtualResourceUri(string file, string topLevelDecl)
    {
        File = file;
        TopLevelDecl = topLevelDecl;
    }

    /// <summary>The Daml file the declaration is in, as a full Windows path.</summary>
    public string File { get; }

    /// <summary>The script's top-level declaration, e.g. <c>setup</c>.</summary>
    public string TopLevelDecl { get; }

    /// <summary>The resource of <paramref name="topLevelDecl"/> in <paramref name="file"/>.</summary>
    /// <exception cref="ArgumentException">The path isn't a full path, or the name is empty.</exception>
    public static VirtualResourceUri For(string file, string topLevelDecl)
    {
        if (string.IsNullOrEmpty(topLevelDecl))
        {
            throw new ArgumentException("A virtual resource needs a declaration name.", nameof(topLevelDecl));
        }
        return new VirtualResourceUri(DamlPath.Normalize(file), topLevelDecl);
    }

    /// <summary>Reads a <c>daml://compiler</c> address.</summary>
    /// <exception cref="FormatException">It isn't one, or lacks the file or the declaration.</exception>
    public static VirtualResourceUri Parse(string text) =>
        TryParse(text, out var uri) ? uri! : throw new FormatException($"'{text}' is not a Daml virtual resource address.");

    /// <summary>Reads a <c>daml://compiler</c> address; false for anything else.</summary>
    public static bool TryParse(string? text, out VirtualResourceUri? uri)
    {
        uri = null;
        if (text is null || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string? file = null;
        string? declaration = null;
        foreach (var parameter in text.Substring(Prefix.Length).Split('&'))
        {
            var equals = parameter.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }
            var name = parameter.Substring(0, equals);
            if (name != "file" && name != "top-level-decl")
            {
                continue;
            }
            if (!TryUnescape(parameter.Substring(equals + 1), out var value))
            {
                return false;
            }
            if (name == "file")
            {
                file = value;
            }
            else
            {
                declaration = value;
            }
        }
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(declaration))
        {
            return false;
        }
        try
        {
            uri = new VirtualResourceUri(DamlPath.Normalize(file!), declaration!);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The address in damlc's own form, forward slashes and all.</summary>
    public override string ToString() =>
        Prefix + "file=" + Uri.EscapeDataString(File.Replace('\\', '/')) + "&top-level-decl=" + Uri.EscapeDataString(TopLevelDecl);

    /// <inheritdoc />
    public bool Equals(VirtualResourceUri? other) =>
        other is not null && DamlPath.Comparer.Equals(File, other.File) && string.Equals(TopLevelDecl, other.TopLevelDecl, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as VirtualResourceUri);

    /// <inheritdoc />
    public override int GetHashCode() =>
        unchecked((DamlPath.Comparer.GetHashCode(File) * 397) ^ StringComparer.Ordinal.GetHashCode(TopLevelDecl));

    // Uri.UnescapeDataString leaves broken escapes as they are; those aren't addresses damlc made.
    private static bool TryUnescape(string escaped, out string value)
    {
        for (var i = escaped.IndexOf('%'); i >= 0; i = escaped.IndexOf('%', i + 1))
        {
            if (i + 2 >= escaped.Length || !IsHex(escaped[i + 1]) || !IsHex(escaped[i + 2]))
            {
                value = "";
                return false;
            }
        }
        value = Uri.UnescapeDataString(escaped);
        return true;
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
