using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Equilibrium.Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// The messages between the results page and the host. The page may only say which view it
/// shows and toggle two checkboxes; anything else, from anywhere else, is refused.
/// </summary>
public static class PageMessages
{
    /// <summary>The page switched between table and transaction view.</summary>
    public const string SetSelectedView = "set_selected_view";

    /// <summary>The page's "Show archived" checkbox changed.</summary>
    public const string SetShowArchived = "set_show_archived";

    /// <summary>The page's "Show detailed disclosure" checkbox changed.</summary>
    public const string SetShowDetailedDisclosure = "set_show_detailed_disclosure";

    /// <summary>The longest message the page has any reason to send.</summary>
    public const int MaxLength = 4096;

    private static readonly JsonSerializerSettings Settings = new() { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };

    /// <summary>Checks a message from the page.</summary>
    /// <param name="source">The URL of the document that sent it.</param>
    /// <param name="json">The message as JSON.</param>
    /// <param name="currentPage">
    /// The address of the page load now shown. A message from an earlier load, still on its way
    /// when the page was replaced, is about a view that no longer exists and is dropped.
    /// </param>
    public static PageMessage Parse(string source, string json, string? currentPage = null)
    {
        if (!ResultsNavigation.IsOwnPage(source))
        {
            return PageMessage.Refused($"a message from {Shorten(source)}");
        }
        if (currentPage is not null && !string.Equals(WithoutAnchor(source), WithoutAnchor(currentPage), StringComparison.OrdinalIgnoreCase))
        {
            return PageMessage.Refused($"a message from an earlier load of the page ({Shorten(source)})");
        }
        if (json is null || json.Length > MaxLength)
        {
            return PageMessage.Refused($"a message of {json?.Length ?? 0} chars");
        }

        JToken message;
        try
        {
            message = JsonConvert.DeserializeObject<JToken>(json, Settings)!;
        }
        catch (JsonException)
        {
            return PageMessage.Refused("a message that isn't JSON");
        }
        if (message is not JObject obj || obj["command"] is not { Type: JTokenType.String } commandToken)
        {
            return PageMessage.Refused($"a message without a command: {Shorten(json)}");
        }

        var command = (string)commandToken!;
        var value = obj["value"];
        var valid = command switch
        {
            SetSelectedView => value?.Type == JTokenType.String && ScriptResultsViewState.IsView((string?)value),
            SetShowArchived or SetShowDetailedDisclosure => value?.Type == JTokenType.Boolean,
            _ => false,
        };
        if (!valid || obj.Properties().Any(p => p.Name is not ("command" or "value")))
        {
            return PageMessage.Refused($"the message {Shorten(json)}");
        }
        return new PageMessage(command, value, null);
    }

    /// <summary>The message that puts the page into <paramref name="state"/>.</summary>
    public static string SetView(ScriptResultsViewState state)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        return new JObject
        {
            ["command"] = "set_view",
            ["value"] = new JObject
            {
                ["selected"] = state.SelectedView,
                ["showArchived"] = state.ShowArchived,
                ["showDetailedDisclosure"] = state.ShowDetailedDisclosure,
            },
        }.ToString(Formatting.None);
    }

    /// <summary>
    /// The message that shows a note above the results. The server writes a note as a small HTML
    /// document; the page puts the value into innerHTML and allows inline handlers for its own
    /// controls, so only the note's text goes in, encoded, styled as a warning when it was one.
    /// </summary>
    public static string AddNote(string text)
    {
        var note = text ?? "";
        var plain = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(note, "<[^>]*>", " ")), @"\s+", " ").Trim();
        var value = WebUtility.HtmlEncode(plain);
        if (note.IndexOf("da-hl-warning", StringComparison.Ordinal) >= 0)
        {
            value = "<span class=\"da-hl-warning\">" + value + "</span>";
        }
        return new JObject { ["command"] = "add_note", ["value"] = value }.ToString(Formatting.None);
    }

    private static string WithoutAnchor(string uri)
    {
        var hash = uri.IndexOf('#');
        return hash < 0 ? uri : uri.Substring(0, hash);
    }

    private static string Shorten(string? text) => text is null ? "nowhere" : text.Length <= 120 ? text : text.Substring(0, 120) + "…";
}

/// <summary>A checked message from the page: a command and its value, or why it was refused.</summary>
/// <param name="Command">The command, when accepted.</param>
/// <param name="Value">The command's value, when accepted.</param>
/// <param name="Rejection">What was refused, for the log; <c>null</c> when accepted.</param>
public sealed record PageMessage(string? Command, JToken? Value, string? Rejection)
{
    internal static PageMessage Refused(string what) => new(null, null, what);
}
