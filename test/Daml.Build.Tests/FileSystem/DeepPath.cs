using System.IO;
using System.Linq;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Tests.FileSystem;

// A generated file deep and long enough to pass Windows' 260-character limit under any temp
// folder, as a Daml module tree and the generator's package registration file easily are.
internal static class DeepPath
{
    public static readonly string Relative = Path.Combine(
        Enumerable.Repeat("LongModuleName", 8).Append("PackageRegistration_" + new string('0', 64) + ".cs").ToArray());

    public static string Write(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(LongPaths.Extended(Path.GetDirectoryName(path)!));
        File.WriteAllText(LongPaths.Extended(path), content);
        return path;
    }

    public static void Delete(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(LongPaths.Extended(folder), recursive: true);
        }
    }
}
