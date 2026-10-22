using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Bindings;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Tests;

// Background sync is one part for the whole session, which the package starts and the option switches on.
public class BindingSyncExportsTests
{
    [Fact, Trait("Category", "Editor")]
    public async Task One_background_sync_serves_the_session()
    {
        var parts = await MefParts.DiscoverAsync();

        var exporting = MefParts.PartsExporting(parts, typeof(BindingSyncService)).ShouldHaveSingleItem();
        exporting.IsShared.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Editor")]
    public void Background_sync_is_off_until_the_user_turns_it_on()
    {
        new Options.DamlOptions().ToSettings().LiveBindingSync.ShouldBeFalse();
    }
}
