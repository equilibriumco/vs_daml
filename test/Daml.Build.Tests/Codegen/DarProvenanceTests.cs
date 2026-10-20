using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Daml.Build.Core.Codegen;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public sealed class DarProvenanceTests : IDisposable
{
    private const string Main = "4b7ecfc366d79ccc5ed07c80f26fe489cf2dfd43ce2856c06a78e6a048db7032";
    private const string Metadata = "4ded6b668cb3b64f7a88a30874cd41c75829f5e064b3fbbadf41ec7e8363354f";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-dar-" + Guid.NewGuid().ToString("N"));

    public DarProvenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A DAR damlc built, as the token standard publishes it.
    private static string TokenHolding => Repository.Sample(@"mixed-dotnet\daml\dars\splice-api-token-holding-v2-1.0.0.dar");

    private string Dar(string? manifest, string name = "made.dar")
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (manifest is not null)
        {
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open(), new UTF8Encoding(false));
            writer.Write(manifest);
        }
        return path;
    }

    [Fact, Trait("Category", "Unit")]
    public void Manifest_only_reads_do_not_decode_the_DALF_but_generation_still_validates_it()
    {
        var entry = "contracts-1.0.0-" + Main + ".dalf";
        var dar = Dar($"Name: contracts-1.0.0\nMain-Dalf: {entry}\n");
        using (var zip = ZipFile.Open(dar, ZipArchiveMode.Update))
        using (var payload = new StreamWriter(zip.CreateEntry(entry).Open()))
        {
            payload.Write("invalid DALF payload");
        }
        DarProvenance.ReadManifest(dar).MainPackageId.ShouldBe(Main);
        Should.Throw<InvalidDataException>(() => DarProvenance.Read(dar));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_DAR_names_its_package_its_sdk_and_the_package_ids_it_holds()
    {
        var provenance = DarProvenance.Read(TokenHolding);

        (provenance.PackageName, provenance.PackageVersion, provenance.SdkVersion, provenance.MainPackageId)
            .ShouldBe(("splice-api-token-holding-v2", "1.0.0", "3.5.2", Main));
        provenance.DependencyPackageIds.ShouldContain(Metadata);
        provenance.DependencyPackageIds.ShouldNotContain(Main);
        provenance.Modules.ShouldContain("Splice.Api.Token.HoldingV2");
        provenance.DependencyPackageIds.ShouldBe(provenance.DependencyPackageIds.Distinct().OrderBy(id => id, StringComparer.Ordinal));
    }

    // A JAR manifest wraps its lines at 72 bytes; a continuation starts with one space.
    [Fact, Trait("Category", "Unit")]
    public void Wrapped_lines_and_crlf_are_read_as_jar_manifests_write_them()
    {
        var id = new string('a', 64);
        var dependency = new string('b', 64);
        var dar = Dar(
            "Manifest-Version: 1.0\r\nName: my-contracts-2.0.1\r\nSdk-Version: 3.6.0\r\n" +
            $"Main-Dalf: my-contracts-2.0.1-{id.Substring(0, 30)}\r\n {id.Substring(30)}/my-contracts-2.0.1-{id}.dalf\r\n" +
            $"Dalfs: my-contracts-2.0.1-{id}/my-contracts-2.0.1-{id}.dalf, my-con\r\n tracts-2.0.1-{id}/daml-prim-{dependency}.dalf\r\n" +
            "Format: daml-lf\r\n");

        var provenance = DarProvenance.Read(dar);

        provenance.ShouldBe(new DarProvenance("my-contracts", "2.0.1", "3.6.0", id, [dependency]));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("Manifest-Version: 1.0\nName: my-contracts-2.0.1\n")]
    [InlineData("Manifest-Version: 1.0\nName: my-contracts-2.0.1\nMain-Dalf: my-contracts-2.0.1/not-a-package-id.dalf\n")]
    public void A_zip_without_a_usable_DAR_manifest_isnt_a_DAR(string? manifest) =>
        Should.Throw<InvalidDataException>(() => DarProvenance.Read(Dar(manifest)));

    [Fact, Trait("Category", "Unit")]
    public void A_file_that_isnt_a_zip_isnt_a_DAR()
    {
        var path = Path.Combine(_root, "text.dar");
        File.WriteAllText(path, "not a DAR");

        Should.Throw<InvalidDataException>(() => DarProvenance.Read(path));
    }
}
