using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

// Discovers the extension's MEF parts the way VS does, so a wrong attribute fails here
// instead of silently doing nothing in the IDE.
public class MefExportsTests
{
    [Fact, Trait("Category", "Editor")]
    public async Task Extension_assembly_has_no_mef_discovery_errors()
    {
        var parts = await DiscoverAsync();

        parts.DiscoveryErrors.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Daml_content_type_derives_from_the_lsp_client_base_type()
    {
        var export = await SingleExportAsync(typeof(ContentTypeDefinition));

        export.Metadata["Name"].ShouldBe("daml");
        ((IEnumerable<string>)export.Metadata["BaseDefinition"]!).ShouldBe(new[] { CodeRemoteContentDefinition.CodeRemoteContentTypeName });
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Daml_files_map_to_the_daml_content_type()
    {
        var export = await SingleExportAsync(typeof(FileExtensionToContentTypeDefinition));

        export.Metadata["FileExtension"].ShouldBe(".daml");
        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(new[] { "daml" });
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Logger_is_exported_once_and_only_needs_the_joinable_task_context()
    {
        var parts = await DiscoverAsync();

        var loggerParts = parts.Parts.Where(p => p.ExportedTypes.Any(e => e.ContractName == ContractNameOf(typeof(IDamlLogger)))).ToList();
        loggerParts.Count.ShouldBe(1);
        loggerParts[0].ImportingConstructorImports!.Select(i => i.ImportDefinition.ContractName)
            .ShouldBe(new[] { "Microsoft.VisualStudio.Threading.JoinableTaskContext" });
    }

    private static async Task<DiscoveredParts> DiscoverAsync()
    {
        var discovery = PartDiscovery.Combine(
            new AttributedPartDiscoveryV1(Resolver.DefaultInstance),
            new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true));
        return await discovery.CreatePartsAsync(typeof(DamlPackage).Assembly);
    }

    private static async Task<ExportDefinition> SingleExportAsync(System.Type contract)
    {
        var parts = await DiscoverAsync();
        return parts.Parts
            .SelectMany(p => p.ExportingMembers.SelectMany(m => m.Value).Concat(p.ExportedTypes))
            .Single(e => e.ContractName == ContractNameOf(contract));
    }

    private static string ContractNameOf(System.Type type) => type.FullName!;
}
