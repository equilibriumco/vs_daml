using System;
using System.Collections.Generic;
using Daml.VisualStudio.Core.Server;
using Newtonsoft.Json.Linq;

namespace Daml.VisualStudio.LanguageServer.Lenses;

/// <summary>A range in a document, zero-based, in UTF-16 code units, end exclusive.</summary>
/// <param name="StartLine">Line the range starts on.</param>
/// <param name="StartCharacter">Column it starts at.</param>
/// <param name="EndLine">Line it ends on.</param>
/// <param name="EndCharacter">Column it ends before.</param>
public readonly record struct LensRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

/// <summary>
/// "Script results" over a script declaration: where it is, what the results window is called,
/// and the virtual resource that produces them.
/// </summary>
/// <param name="Range">The declaration the lens belongs to.</param>
/// <param name="Title">The results' title as damlc names it, e.g. <c>Script: setup</c>.</param>
/// <param name="Resource">The address to open for the results.</param>
public sealed record ScriptLens(LensRange Range, string Title, VirtualResourceUri Resource);

/// <summary>Reads damlc's answer to <c>textDocument/codeLens</c>.</summary>
public static class ScriptLensParser
{
    /// <summary>The command damlc puts on a script's lens.</summary>
    public const string ShowResourceCommand = "daml.showResource";

    /// <summary>
    /// The script lenses in <paramref name="result"/>. Lenses with another command, without one,
    /// or with arguments that don't name a results title and address are skipped.
    /// </summary>
    public static IReadOnlyList<ScriptLens> Parse(JToken? result)
    {
        var lenses = new List<ScriptLens>();
        if (result is not JArray items)
        {
            return lenses;
        }
        foreach (var item in items)
        {
            if (item is not JObject lens
                || lens["command"] is not JObject command
                || (string?)command["command"] != ShowResourceCommand
                || command["arguments"] is not JArray { Count: >= 2 } arguments
                || arguments[0] is not JValue { Type: JTokenType.String } title
                || arguments[1] is not JValue { Type: JTokenType.String } address
                || !VirtualResourceUri.TryParse((string?)address, out var resource)
                || ReadRange(lens["range"]) is not { } range)
            {
                continue;
            }
            lenses.Add(new ScriptLens(range, (string)title!, resource!));
        }
        return lenses;
    }

    private static LensRange? ReadRange(JToken? range)
    {
        if (range is not JObject || range["start"] is not JObject start || range["end"] is not JObject end
            || Number(start["line"]) is not { } startLine || Number(start["character"]) is not { } startCharacter
            || Number(end["line"]) is not { } endLine || Number(end["character"]) is not { } endCharacter
            || endLine < startLine || (endLine == startLine && endCharacter < startCharacter))
        {
            return null;
        }
        return new LensRange(startLine, startCharacter, endLine, endCharacter);
    }

    private static int? Number(JToken? token) =>
        token is JValue { Type: JTokenType.Integer } value && (long)value is >= 0 and <= int.MaxValue ? (int)(long)value : null;
}

/// <summary>The lenses of a document have changed.</summary>
public sealed class ScriptLensesChangedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public ScriptLensesChangedEventArgs(string documentUri, IReadOnlyList<ScriptLens> lenses)
    {
        DocumentUri = documentUri;
        Lenses = lenses;
    }

    /// <summary>The document, as VS sent its URI.</summary>
    public string DocumentUri { get; }

    /// <summary>Its lenses now; empty when it has none or the request failed.</summary>
    public IReadOnlyList<ScriptLens> Lenses { get; }
}
