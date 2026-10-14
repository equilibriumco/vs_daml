using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Lenses;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

// The lens rows are three editor parts that must agree: room above a line, the row drawn in
// it, and Ctrl+. on the same line. All of them for Daml documents only.
public class LensExportsTests
{
    [Theory, Trait("Category", "Editor")]
    [InlineData(typeof(ILineTransformSourceProvider), typeof(DamlLensSpaceProvider))]
    [InlineData(typeof(IWpfTextViewCreationListener), typeof(DamlLensRowViewFactory))]
    [InlineData(typeof(ISuggestedActionsSourceProvider), typeof(DamlLensActionsProvider))]
    public async Task Each_lens_part_is_exported_for_daml_documents(System.Type contract, System.Type part)
    {
        var parts = await MefParts.DiscoverAsync();

        var definition = MefParts.PartsExporting(parts, contract).Single(p => p.Type.FullName == part.FullName);

        var export = definition.ExportedTypes.Single(e => e.ContractName == MefParts.ContractName(contract));
        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe([DamlContentDefinition.ContentTypeName]);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task The_sources_are_one_shared_part()
    {
        var parts = await MefParts.DiscoverAsync();

        MefParts.PartsExporting(parts, typeof(DamlLensSources)).ShouldHaveSingleItem().IsShared.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Editor")]
    public async Task The_rows_get_their_own_adornment_layer_above_the_text()
    {
        var layer = (await MefParts.ExportsAsync(typeof(AdornmentLayerDefinition)))
            .Single(e => Equals(e.Metadata["Name"], DamlLensRowViewFactory.LayerName));

        ((IEnumerable<string>)layer.Metadata["After"]!).ShouldBe([PredefinedAdornmentLayers.Text]);
    }
}
