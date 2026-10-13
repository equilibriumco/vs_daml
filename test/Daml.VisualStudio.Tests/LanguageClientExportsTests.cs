using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

// VS finds the language client purely through these attributes; a wrong one means no server.
public class LanguageClientExportsTests
{
    [Fact, Trait("Category", "Editor")]
    public async Task Exactly_one_language_client_is_exported_for_daml_content()
    {
        var parts = await MefParts.DiscoverAsync();

        var exports = parts.Parts.SelectMany(p => p.ExportedTypes).Where(e => e.ContractName == typeof(ILanguageClient).FullName).ToList();

        var export = exports.ShouldHaveSingleItem();
        ((IEnumerable<string>)export.Metadata["ContentTypes"]!).ShouldBe(["daml"]);
    }

    // Menu commands reach the running server through it; one part, so the same shared instance.
    [Fact, Trait("Category", "Editor")]
    public async Task The_language_client_part_is_also_exported_as_itself_for_commands()
    {
        var parts = await MefParts.DiscoverAsync();

        var client = parts.Parts.Single(p => p.ExportedTypes.Any(e => e.ContractName == typeof(ILanguageClient).FullName));

        client.ExportedTypes.Select(e => e.ContractName).ShouldContain(typeof(Daml.VisualStudio.LanguageClient.DamlLanguageClient).FullName);
        client.IsShared.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Editor")]
    public async Task The_language_client_only_imports_services_every_VS_has()
    {
        var parts = await MefParts.DiscoverAsync();

        var client = parts.Parts.Single(p => p.ExportedTypes.Any(e => e.ContractName == typeof(ILanguageClient).FullName));

        client.ImportingConstructorImports!.Select(i => i.ImportDefinition.ContractName)
            .ShouldBe(["Microsoft.VisualStudio.Threading.JoinableTaskContext", "Daml.VisualStudio.Core.Logging.IDamlLogger"]);
    }

}
