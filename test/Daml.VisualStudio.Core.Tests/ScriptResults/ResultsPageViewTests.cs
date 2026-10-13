using Daml.VisualStudio.Core.ScriptResults;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.ScriptResults;

public class ResultsPageViewTests
{
    private const string WithTable = """<div class="table"><table></table></div><div class="da-hl-transaction">tx</div>""";
    private const string WithoutTable = """<div class="da-hl-transaction">tx</div>""";

    [Fact, Trait("Category", "Unit")]
    public void Results_with_contracts_open_on_the_view_the_user_chose()
    {
        ResultsPageView.For(ScriptResultsViewState.Default, WithTable).ShouldBe(ScriptResultsViewState.Default);
        var transaction = ScriptResultsViewState.Default with { SelectedView = ScriptResultsViewState.Transaction };
        ResultsPageView.For(transaction, WithTable).ShouldBe(transaction);
    }

    [Fact, Trait("Category", "Unit")]
    public void Results_without_a_contract_table_open_on_the_transaction_view_keeping_the_checkboxes()
    {
        var chosen = ScriptResultsViewState.Default with { ShowArchived = true };

        var shown = ResultsPageView.For(chosen, WithoutTable);

        shown.SelectedView.ShouldBe(ScriptResultsViewState.Transaction);
        shown.ShowArchived.ShouldBeTrue();
        chosen.SelectedView.ShouldBe(ScriptResultsViewState.Table, "the user's choice stays for the next results");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_waiting_page_keeps_the_choice()
    {
        ResultsPageView.For(ScriptResultsViewState.Default, null).ShouldBe(ScriptResultsViewState.Default);
    }
}
