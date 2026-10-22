using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Equilibrium.Daml.Build.Core.Codegen;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Codegen;

public class GenerationIdentityTests
{
    private const string Digest = "9f040219936298f085e2ade77fbbc37829df065b879633282aa4628c9a1b6548";
    private const string OtherDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Contracts = @"C:\work\Contracts\Contracts.msbuildproj";

    private static string Id(DamlReferenceKind kind, string path, string? @namespace, string digest = Digest) =>
        GenerationIdentity.Id(kind, path, @namespace, digest);

    // Ids name folders on disk that later builds must find again, so the recipe is fixed here,
    // not just whatever the code happens to compute.
    [Fact, Trait("Category", "Unit")]
    public void The_id_is_the_start_of_a_sha256_over_kind_path_namespace_and_generator()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes("daml-generation-id/1\nproject\n" + Contracts.ToUpperInvariant() + "\nContracts.Gen\n" + Digest));
        var hash = sha.GetHashAndReset();
        var expected = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            expected.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        Id(DamlReferenceKind.Project, Contracts, "Contracts.Gen").ShouldBe(expected.ToString());
    }

    [Fact, Trait("Category", "Unit")]
    public void Letter_case_of_the_path_doesnt_matter_but_of_the_namespace_does()
    {
        Id(DamlReferenceKind.Project, Contracts.ToLowerInvariant(), "Gen").ShouldBe(Id(DamlReferenceKind.Project, Contracts, "Gen"));
        Id(DamlReferenceKind.Project, Contracts, "gen").ShouldNotBe(Id(DamlReferenceKind.Project, Contracts, "Gen"));
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_input_changes_the_id()
    {
        var id = Id(DamlReferenceKind.Project, Contracts, null);

        new[]
        {
            Id(DamlReferenceKind.Dar, Contracts, null),
            Id(DamlReferenceKind.Project, @"C:\work\Contracts2\Contracts.msbuildproj", null),
            Id(DamlReferenceKind.Project, Contracts, "Gen"),
            Id(DamlReferenceKind.Project, Contracts, null, OtherDigest),
        }.ShouldAllBe(other => other != id);
        id.Length.ShouldBe(16);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\Contracts\Contracts.msbuildproj", "Contracts")]
    [InlineData(@"C:\vendor\finance-1.0.0.dar", "finance-1.0.0")]
    [InlineData(@"C:\vendor\my contracts (v2).dar", "my_contracts__v2_")]
    [InlineData(@"C:\vendor\Zażółć.dar", "Za____")]
    [InlineData(@"C:\vendor\a-very-long-package-name-that-goes-on-and-on.dar", "a-very-long-package-name-that-go")]
    [InlineData(@"C:\vendor\.dar", "reference")]
    public void The_folder_is_named_after_the_reference_and_its_id(string path, string label) =>
        GenerationIdentity.FolderName(path, "0123456789abcdef").ShouldBe(label + "-0123456789abcdef");

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\Contracts\Contracts.msbuildproj")]
    [InlineData(@"C:\vendor\a-very-long-package-name-that-goes-on-and-on.dar")]
    public void The_id_is_read_back_from_the_folder_name(string path) =>
        GenerationIdentity.IdOfFolder(GenerationIdentity.FolderName(path, "0123456789abcdef")).ShouldBe("0123456789abcdef");

    [Theory, Trait("Category", "Unit")]
    [InlineData("Contracts")]
    [InlineData("Contracts-0123")]
    [InlineData("Contracts-0123456789ABCDEF")]
    public void A_name_without_an_id_isnt_a_generations_folder(string folderName) =>
        Should.Throw<ArgumentException>(() => GenerationIdentity.IdOfFolder(folderName));
}
