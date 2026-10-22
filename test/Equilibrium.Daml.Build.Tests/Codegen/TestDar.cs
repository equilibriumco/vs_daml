using System.IO;
using System.IO.Compression;
using System.Text;

namespace Equilibrium.Daml.Build.Tests.Codegen;

// The smallest DAR the build reads: a zip with a manifest naming its package and its dalfs.
internal static class TestDar
{
    public const string MainPackageId = "1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb";
    public const string DependencyPackageId = "4b7ecfc366d79ccc5ed07c80f26fe489cf2dfd43ce2856c06a78e6a048db7032";

    public static void Write(string path, string package = "contracts-1.2.0")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open(), new UTF8Encoding(false));
        writer.Write(
            $"Manifest-Version: 1.0\nName: {package}\nSdk-Version: 3.6.0\n" +
            $"Main-Dalf: {package}-{MainPackageId}/{package}-{MainPackageId}.dalf\n" +
            $"Dalfs: {package}-{MainPackageId}/{package}-{MainPackageId}.dalf, {package}-{MainPackageId}/splice-api-token-holding-v2-1.0.0-{DependencyPackageId}.dalf\n");
    }
}
