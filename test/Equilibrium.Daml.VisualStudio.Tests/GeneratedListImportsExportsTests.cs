using System.Linq;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Bindings;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Tests;

// One part for the session looks after the evaluations that missed their list of generated files;
// the binding service, which every build and background update reaches, asks it to.
public class GeneratedListImportsExportsTests
{
    [Fact, Trait("Category", "Editor")]
    public async Task One_shared_part_catches_up_the_evaluations_and_the_binding_service_uses_it()
    {
        var parts = await MefParts.DiscoverAsync();

        var imports = MefParts.PartsExporting(parts, typeof(GeneratedListImports)).ShouldHaveSingleItem();
        imports.IsShared.ShouldBeTrue();
        imports.ImportingConstructorOrFactory.ShouldNotBeNull();
        imports.Imports.Select(i => i.ImportDefinition.ContractName).ShouldContain(MefParts.ContractName(typeof(Microsoft.VisualStudio.ProjectSystem.IProjectServiceAccessor)));
        MefParts.PartsExporting(parts, typeof(DamlBindingService)).ShouldHaveSingleItem()
            .Imports.Select(i => i.ImportDefinition.ContractName).ShouldContain(MefParts.ContractName(typeof(GeneratedListImports)));
    }
}
