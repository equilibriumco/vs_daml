using System;
using System.IO;
using Daml.Build.Core.FileSystem;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// A results page link to a place in a Daml source: the file and the lines to select, from
/// the start of <see cref="StartLine"/> to the start of <see cref="EndLine"/> (the whole line
/// when they are equal). Lines are zero-based.
/// </summary>
/// <param name="File">The Daml file, as a full Windows path.</param>
/// <param name="StartLine">The first line.</param>
/// <param name="EndLine">The line the selection ends at the start of.</param>
public sealed record RevealRequest(string File, int StartLine, int EndLine)
{
    /// <summary>The link prefix damlc writes before the URL-encoded JSON arguments.</summary>
    public const string Prefix = "command:daml.revealLocation?";

    /// <summary>The longest link that is read at all.</summary>
    public const int MaxLength = 4096;

    // No Daml file is anywhere near this long; a bigger number is not a line.
    private const int MaxLine = 1_000_000;

    private static readonly JsonSerializerSettings Settings = new() { DateParseHandling = DateParseHandling.None, MaxDepth = 4 };

    /// <summary>
    /// True when the document has the lines: the first one exists, and the selection ends no
    /// later than the end of the document.
    /// </summary>
    public bool FitsIn(int lineCount) => StartLine < lineCount && EndLine <= lineCount;

    /// <summary>
    /// Reads a <c>command:daml.revealLocation?["file://…", start, end]</c> link. Only a Daml
    /// file given by a full local or network path, with no <c>.</c>/<c>..</c> segments, query,
    /// fragment or stream, and a plausible range, is accepted; anything else gets a reason.
    /// </summary>
    public static bool TryParse(string? link, out RevealRequest? request, out string? rejection)
    {
        request = null;
        rejection = Check(link, out var parsed);
        request = parsed;
        return rejection is null;
    }

    private static string? Check(string? link, out RevealRequest? request)
    {
        request = null;
        if (link is null || !link.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return "not a source link";
        }
        if (link.Length > MaxLength)
        {
            return $"a source link {link.Length} characters long";
        }
        if (!TryUnescape(link.Substring(Prefix.Length), out var json))
        {
            return "a source link with a broken escape";
        }

        JArray arguments;
        try
        {
            if (JsonConvert.DeserializeObject<JToken>(json, Settings) is not JArray array)
            {
                return "a source link whose arguments aren't a list";
            }
            arguments = array;
        }
        catch (JsonException)
        {
            return "a source link whose arguments aren't JSON";
        }
        if (arguments.Count != 3
            || arguments[0] is not JValue { Type: JTokenType.String } url
            || arguments[1] is not JValue { Type: JTokenType.Integer } start
            || arguments[2] is not JValue { Type: JTokenType.Integer } end)
        {
            return "a source link without a file and two line numbers";
        }

        var first = (long)start;
        var last = (long)end;
        if (first < 0 || last < first || last > MaxLine)
        {
            return $"a source link to lines {first}–{last}";
        }
        if (FileFromUrl((string)url!) is not { } file)
        {
            return $"a source link to {Shorten((string)url!)}, which isn't a Daml file on this machine";
        }
        request = new RevealRequest(file, (int)first, (int)last);
        return null;
    }

    // file:///C:/x, file://C:/x (damlc's spelling) and file://server/share/x; nothing else.
    private static string? FileFromUrl(string url)
    {
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || url.IndexOfAny(['?', '#', '\\']) >= 0)
        {
            return null;
        }
        var rest = url.Substring("file:".Length);
        if (!rest.StartsWith("//", StringComparison.Ordinal) || !TryUnescape(rest.Substring(2), out var path))
        {
            return null;
        }
        if (path.Length >= 3 && path[0] == '/' && IsDrive(path, 1))
        {
            path = path.Substring(1);
        }
        string windows;
        if (IsDrive(path, 0))
        {
            windows = path.Replace('/', '\\');
        }
        else if (path.Length > 0 && path[0] != '/' && path.IndexOf('/') > 0)
        {
            windows = @"\\" + path.Replace('/', '\\'); // a server and share
        }
        else
        {
            return null;
        }

        // A colon anywhere but after the drive letter names a stream or a device.
        var colon = windows.IndexOf(':', IsDrive(windows, 0) ? 2 : 0);
        if (colon >= 0 || windows.StartsWith(@"\\?\", StringComparison.Ordinal) || windows.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return null;
        }
        foreach (var segment in windows.Split('\\'))
        {
            if (segment is "." or "..")
            {
                return null;
            }
        }
        if (!string.Equals(Path.GetExtension(windows), ".daml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            return DamlPath.Normalize(windows);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsDrive(string path, int at) =>
        path.Length >= at + 3 && char.IsLetter(path[at]) && path[at] < 128 && path[at + 1] == ':' && (path[at + 2] == '/' || path[at + 2] == '\\');

    // Uri.UnescapeDataString leaves a broken escape as it is; such a link wasn't written by damlc.
    private static bool TryUnescape(string escaped, out string value)
    {
        for (var i = escaped.IndexOf('%'); i >= 0; i = escaped.IndexOf('%', i + 1))
        {
            if (i + 2 >= escaped.Length || !Uri.IsHexDigit(escaped[i + 1]) || !Uri.IsHexDigit(escaped[i + 2]))
            {
                value = "";
                return false;
            }
        }
        value = Uri.UnescapeDataString(escaped);
        return true;
    }

    private static string Shorten(string text) => text.Length <= 120 ? text : text.Substring(0, 120) + "…";
}
