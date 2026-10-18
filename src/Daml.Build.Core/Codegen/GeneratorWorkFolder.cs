using System.IO;
using Daml.Build.Core.Configuration;

namespace Daml.Build.Core.Codegen;

/// <summary>The folder dpm runs the C# code generator in, for an install or a run.</summary>
internal static class GeneratorWorkFolder
{
    /// <summary>
    /// Writes the daml.yaml that names only <paramref name="pin"/>, and an empty multi-package.yaml:
    /// dpm looks for one above its working folder, and a user's with an sdk-version (as dpm new
    /// makes it) would clash with the component. The one here ends that search.
    /// </summary>
    public static void Prepare(string folder, CodegenComponentPin pin)
    {
        File.WriteAllText(Path.Combine(folder, "daml.yaml"), $"name: daml-codegen\nversion: 0.0.1\ncomponents:\n  - {pin.Reference}\n");
        File.WriteAllText(Path.Combine(folder, "multi-package.yaml"), "packages: []\n");
    }
}
