using System;
using Newtonsoft.Json.Linq;

namespace Equilibrium.Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// What the results page is showing: the table or the transaction view, and whether archived
/// contracts and detailed disclosure are visible. The page forgets this on every reload (new
/// results, a theme change), so the host keeps it and sends it back.
/// </summary>
/// <param name="SelectedView"><c>table</c> or <c>transaction</c>.</param>
/// <param name="ShowArchived">Archived contracts shown in the table.</param>
/// <param name="ShowDetailedDisclosure">Per-party disclosure columns shown.</param>
public sealed record ScriptResultsViewState(string SelectedView, bool ShowArchived, bool ShowDetailedDisclosure)
{
    /// <summary>The table view.</summary>
    public const string Table = "table";

    /// <summary>The transaction view.</summary>
    public const string Transaction = "transaction";

    /// <summary>How the page starts: the table, without archived contracts or disclosure.</summary>
    public static ScriptResultsViewState Default { get; } = new(Table, ShowArchived: false, ShowDetailedDisclosure: false);

    /// <summary>True for the two views the page has.</summary>
    public static bool IsView(string? view) => view is Table or Transaction;

    /// <summary>The state after a message <see cref="PageMessages.Parse"/> accepted.</summary>
    public ScriptResultsViewState Apply(string command, JToken value) => command switch
    {
        PageMessages.SetSelectedView => this with { SelectedView = (string)value! },
        PageMessages.SetShowArchived => this with { ShowArchived = (bool)value },
        PageMessages.SetShowDetailedDisclosure => this with { ShowDetailedDisclosure = (bool)value },
        _ => throw new ArgumentException($"'{command}' isn't a command the page sends.", nameof(command)),
    };
}
