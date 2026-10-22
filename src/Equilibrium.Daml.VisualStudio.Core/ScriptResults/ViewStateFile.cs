using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Equilibrium.Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// The saved views of every results window of a workspace, so a script's results open the way
/// they were left. A file that can't be read is treated as no saved views: losing a checkbox is
/// better than failing to show results.
/// </summary>
public static class ViewStateFile
{
    /// <summary>How many views are kept; the oldest go first.</summary>
    public const int MaxViews = 200;

    private const int Version = 1;
    private static readonly JsonSerializerSettings Settings = new() { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };

    /// <summary>The views saved in <paramref name="text"/>.</summary>
    public static IReadOnlyDictionary<string, ScriptResultsViewState> Read(string text)
    {
        var views = new Dictionary<string, ScriptResultsViewState>(StringComparer.Ordinal);
        JToken? root;
        try
        {
            root = string.IsNullOrWhiteSpace(text) ? null : JsonConvert.DeserializeObject<JToken>(text, Settings);
        }
        catch (JsonException)
        {
            return views;
        }
        if (root is not JObject file || file["version"] is not { Type: JTokenType.Integer } version || !JToken.DeepEquals(version, new JValue(Version))
            || file["views"] is not JObject saved)
        {
            return views;
        }
        foreach (var entry in saved.Properties())
        {
            if (entry.Value is JObject view
                && view["selected"] is { Type: JTokenType.String } selected && ScriptResultsViewState.IsView((string?)selected)
                && view["showArchived"] is { Type: JTokenType.Boolean } archived
                && view["showDetailedDisclosure"] is { Type: JTokenType.Boolean } disclosure)
            {
                views[entry.Name] = new ScriptResultsViewState((string)selected!, (bool)archived, (bool)disclosure);
            }
        }
        return views;
    }

    /// <summary>
    /// The file for <paramref name="views"/>, oldest first; past <see cref="MaxViews"/> the
    /// oldest are left out.
    /// </summary>
    public static string Write(IEnumerable<KeyValuePair<string, ScriptResultsViewState>> views)
    {
        if (views is null)
        {
            throw new ArgumentNullException(nameof(views));
        }
        var all = views.ToList();
        var saved = new JObject();
        foreach (var view in all.Skip(Math.Max(0, all.Count - MaxViews)))
        {
            saved[view.Key] = new JObject
            {
                ["selected"] = view.Value.SelectedView,
                ["showArchived"] = view.Value.ShowArchived,
                ["showDetailedDisclosure"] = view.Value.ShowDetailedDisclosure,
            };
        }
        return new JObject { ["version"] = Version, ["views"] = saved }.ToString(Formatting.Indented);
    }
}
