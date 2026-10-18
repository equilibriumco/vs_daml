using Daml.Build.Core.Codegen;
using Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Bindings;

public class GenerationProvenanceTests
{
    private const string Package = "bd2aa85e017dad908dbd9c8103929df3edaa7b0961b675d3ef216644565f9cb1";
    private const string Other = "1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb";
    private const string Folder = @"C:\work\App\obj\Debug\net10.0\Daml\Contracts-9de80d6cfe50bada";

    internal static GenerationManifest Manifest(string? @namespace = "Fixture", string package = Package, bool withPackage = true) =>
        new("9de80d6cfe50bada", "f00d", @"C:\work\App\App.csproj", DamlReferenceKind.Project, @"C:\work\Contracts\Contracts.msbuildproj",
            @namespace, @"C:\work\Contracts\.daml\dist\kinds-1.0.0.dar", "da7a", "oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:9f04", [])
        {
            Package = withPackage ? new DarProvenance("kinds", "1.0.0", "3.6.0", package, ["4b7e"]) : null,
            Source = new SourceProvenance(@"C:\work\Contracts\daml.yaml", "50c"),
        };

    private static GenerationProvenance Generation(string? @namespace = "Fixture", string package = Package) =>
        GenerationProvenance.From(Folder, Manifest(@namespace, package)).ShouldNotBeNull();

    private static DeclaredDamlName Data(string @namespace, string entity, string? member = null, DamlSymbolKind kind = DamlSymbolKind.Record) =>
        new(kind, null, null, entity, member, @namespace);

    private static DeclaredDamlName Template(string @namespace, string package = Package, string module = "Kinds.Asset", string? member = null, DamlSymbolKind kind = DamlSymbolKind.Template) =>
        new(kind, package, module, "Asset", member, @namespace);

    [Fact, Trait("Category", "Unit")]
    public void The_manifest_tells_where_the_generation_came_from()
    {
        var generation = Generation();

        (generation.Folder, generation.GenerationId, generation.MainPackageId, generation.Namespace).ShouldBe((Folder, "9de80d6cfe50bada", Package, "Fixture"));
        generation.DependencyPackageIds.ShouldBe(["4b7e"]);
        (generation.Owner, generation.Reference, generation.ReferenceKind).ShouldBe((@"C:\work\App\App.csproj", @"C:\work\Contracts\Contracts.msbuildproj", DamlReferenceKind.Project));
        generation.Source.ShouldBe(new SourceProvenance(@"C:\work\Contracts\daml.yaml", "50c"));
    }

    // Without the DAR's package recorded there's nothing to tell which package the code is of.
    [Fact, Trait("Category", "Unit")]
    public void A_manifest_without_the_DARs_package_gives_no_provenance() =>
        GenerationProvenance.From(Folder, Manifest(withPackage: false)).ShouldBeNull();

    [Fact, Trait("Category", "Unit")]
    public void A_data_type_is_of_the_generations_package_in_the_module_its_namespace_names_under_the_prefix()
    {
        var resolution = Generation().Resolve(Data("Fixture.Kinds.Types", "Point", "x", DamlSymbolKind.Field));

        resolution.Symbol.ShouldBe(new DamlSymbolId(Package, "Kinds.Types", "Point", "x", DamlSymbolKind.Field));
        resolution.Problem.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_a_prefix_the_namespace_is_the_module() =>
        Generation(@namespace: null).Resolve(Data("Kinds.Types", "Shape", "Circle", DamlSymbolKind.Constructor))
            .Symbol.ShouldBe(new DamlSymbolId(Package, "Kinds.Types", "Shape", "Circle", DamlSymbolKind.Constructor));

    [Fact, Trait("Category", "Unit")]
    public void A_template_keeps_what_its_code_states_when_the_generation_agrees()
    {
        Generation().Resolve(Template("Fixture.Kinds.Asset", member: "Give", kind: DamlSymbolKind.Choice))
            .Symbol.ShouldBe(new DamlSymbolId(Package, "Kinds.Asset", "Asset", "Give", DamlSymbolKind.Choice));
        Generation(@namespace: null).Resolve(Template("Kinds.Asset"))
            .Symbol.ShouldBe(new DamlSymbolId(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template));
    }

    // The code is from another build than the manifest: the binding can't be trusted.
    [Fact, Trait("Category", "Unit")]
    public void A_template_of_another_package_than_the_generations_is_rejected()
    {
        var resolution = Generation().Resolve(Template("Fixture.Kinds.Asset", package: Other));

        resolution.Symbol.ShouldBeNull();
        resolution.Problem.ShouldNotBeNull().ShouldContain(Other);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Fixture.Kinds.Other", "Kinds.Asset")]
    [InlineData("Kinds.Asset", "Kinds.Asset")]
    [InlineData("Fixture.Kinds.Asset", "Asset")]
    public void A_template_whose_namespace_isnt_its_module_under_the_prefix_is_rejected(string @namespace, string module)
    {
        var resolution = Generation().Resolve(Template(@namespace, module: module));

        resolution.Symbol.ShouldBeNull();
        resolution.Problem.ShouldNotBeNull().ShouldContain(@namespace);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Other.Kinds.Types")]
    [InlineData("Fixture")]
    [InlineData("FixtureX.Kinds")]
    [InlineData("")]
    public void A_data_type_outside_the_prefix_is_rejected(string @namespace)
    {
        var resolution = Generation().Resolve(Data(@namespace, "Point"));

        resolution.Symbol.ShouldBeNull();
        resolution.Problem.ShouldNotBeNull();
    }
}
