using System.Collections.Generic;
using System.Linq;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Codegen;

public class GenerationReferencesTests
{
    private const string Project = @"C:\work\App";
    private const string Digest = "9f040219936298f085e2ade77fbbc37829df065b879633282aa4628c9a1b6548";

    private static (IReadOnlyList<GenerationReference> References, List<ConfigurationError> Errors) Resolve(params DeclaredDamlReference[] declared)
    {
        var errors = new List<ConfigurationError>();
        return (GenerationReferences.Resolve(declared, Project, Digest, errors), errors);
    }

    private static DeclaredDamlReference Daml(string include, string? @namespace = null) => new(DamlReferenceSource.DamlReference, include, @namespace);

    private static DeclaredDamlReference Dar(string include, string? @namespace = null) => new(DamlReferenceSource.DamlDar, include, @namespace);

    private static DeclaredDamlReference Plain(string include, string? @namespace = null) => new(DamlReferenceSource.ProjectReference, include, @namespace);

    [Fact, Trait("Category", "Unit")]
    public void References_resolve_against_the_project_in_the_order_they_were_declared()
    {
        var (references, errors) = Resolve(Daml(@"..\Contracts\Contracts.msbuildproj", " Contracts "), Dar(@"C:\vendor\finance-1.0.0.dar", ""));

        errors.ShouldBeEmpty();
        references.Select(r => (r.Kind, r.Path, r.Namespace)).ShouldBe(
        [
            (DamlReferenceKind.Project, @"C:\work\Contracts\Contracts.msbuildproj", "Contracts"),
            (DamlReferenceKind.Dar, @"C:\vendor\finance-1.0.0.dar", null),
        ]);
        var first = references[0];
        first.GenerationId.ShouldBe(GenerationIdentity.Id(DamlReferenceKind.Project, first.Path, "Contracts", Digest));
        first.FolderName.ShouldBe("Contracts-" + first.GenerationId);
        first.Include.ShouldBe(@"..\Contracts\Contracts.msbuildproj");
    }

    // Adding a reference in Visual Studio writes a ProjectReference; listing the same project as
    // a DamlReference as well is one reference, and the DamlReference says how.
    [Fact, Trait("Category", "Unit")]
    public void A_project_listed_both_ways_is_one_reference_described_by_its_DamlReference()
    {
        var (references, errors) = Resolve(
            Plain(@"..\Contracts\Contracts.msbuildproj", "Ignored"),
            Daml(@"C:\WORK\Contracts\Contracts.msbuildproj", "Contracts"));

        errors.ShouldBeEmpty();
        var reference = references.ShouldHaveSingleItem();
        reference.Source.ShouldBe(DamlReferenceSource.DamlReference);
        reference.Namespace.ShouldBe("Contracts");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_plain_project_reference_generates_with_its_DamlNamespace() =>
        Resolve(Plain(@"..\Contracts\Contracts.msbuildproj", "Gen")).References.ShouldHaveSingleItem().Namespace.ShouldBe("Gen");

    [Fact, Trait("Category", "Unit")]
    public void One_project_under_two_namespaces_is_two_generations()
    {
        var (references, errors) = Resolve(Daml(@"..\Contracts\Contracts.msbuildproj", "A"), Daml(@"..\Contracts\Contracts.msbuildproj", "B"));

        errors.ShouldBeEmpty();
        references.Select(r => r.GenerationId).Distinct().Count().ShouldBe(2);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(DamlReferenceSource.DamlReference, @"..\Contracts\Contracts.msbuildproj")]
    [InlineData(DamlReferenceSource.DamlDar, @"C:\vendor\finance.dar")]
    [InlineData(DamlReferenceSource.ProjectReference, @"..\Contracts\Contracts.msbuildproj")]
    public void The_same_reference_twice_with_the_same_namespace_is_an_output_conflict(DamlReferenceSource source, string include)
    {
        var (references, errors) = Resolve(new DeclaredDamlReference(source, include, "Gen"), new DeclaredDamlReference(source, include.ToUpperInvariant(), "Gen"));

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.OutputConflict);
        error.Message.ShouldContain("Gen");
        references.ShouldHaveSingleItem();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_DamlReference_to_a_dar_points_to_DamlDar()
    {
        var (references, errors) = Resolve(Daml(@"C:\vendor\finance.dar"));

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        error.Message.ShouldContain("DamlDar");
        references.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_DamlDar_has_to_be_a_dar()
    {
        var (references, errors) = Resolve(Dar(@"..\Contracts\Contracts.msbuildproj"));

        errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        references.ShouldBeEmpty();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Contracts.Gen")]
    [InlineData("_Internal.V2")]
    [InlineData("Umowy.Zażółć")]
    public void A_namespace_is_dotted_csharp_identifiers(string @namespace) =>
        Resolve(Daml(@"..\Contracts\Contracts.msbuildproj", @namespace)).Errors.ShouldBeEmpty();

    [Theory, Trait("Category", "Unit")]
    [InlineData("My Contracts")]
    [InlineData("2Contracts")]
    [InlineData("Contracts..Gen")]
    [InlineData("Contracts.")]
    [InlineData("global::Contracts")]
    public void Anything_else_as_a_namespace_is_refused_before_it_breaks_the_generated_code(string @namespace)
    {
        var (references, errors) = Resolve(Daml(@"..\Contracts\Contracts.msbuildproj", @namespace));

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        error.Message.ShouldContain($"'{@namespace}'");
        error.Message.ShouldContain(@"..\Contracts\Contracts.msbuildproj");
        references.ShouldBeEmpty();
    }
}
