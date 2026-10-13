namespace Daml.VisualStudio.Core.ScriptResults;

/// <summary>Which view a results page should open on, given what the results contain.</summary>
public static class ResultsPageView
{
    /// <summary>
    /// The state to send to a page showing <paramref name="html"/>: the one the user chose, except
    /// that results without a contract table (a script that created nothing) open on the
    /// transaction view, since the table view would be empty. The user's choice itself is kept.
    /// </summary>
    public static ScriptResultsViewState For(ScriptResultsViewState chosen, string? html) =>
        chosen.SelectedView == ScriptResultsViewState.Table && html is not null && html.IndexOf("class=\"table\"", System.StringComparison.Ordinal) < 0
            ? chosen with { SelectedView = ScriptResultsViewState.Transaction }
            : chosen;
}
