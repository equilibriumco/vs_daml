using System.IO;
using Equilibrium.Daml.Build.Core.Configuration;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>The folder dpm runs the C# code generator in, for an install or a run.</summary>
internal static class GeneratorWorkFolder
{
    /// <summary>
    /// Writes the daml.yaml that names only <paramref name="pin"/>, for <c>dpm install package</c>.
    /// Nothing else: beside a multi-package.yaml dpm installs the components of the packages it
    /// lists, and with none listed it succeeds without downloading anything. The install folder lies
    /// in the generator cache, away from the user's workspaces.
    /// </summary>
    public static void PrepareInstall(string folder, CodegenComponentPin pin) =>
        File.WriteAllText(Path.Combine(folder, "daml.yaml"), DamlYaml(pin));

    /// <summary>
    /// Writes the daml.yaml that names only <paramref name="pin"/>, and an empty multi-package.yaml,
    /// for <c>dpm codegen-cs</c>. The run folder lies in the project's obj folder, and dpm looks for a
    /// multi-package.yaml above its working folder: a user's with an sdk-version (as dpm new makes
    /// it) would clash with the component. The one here ends that search.
    /// </summary>
    public static void PrepareRun(string folder, CodegenComponentPin pin)
    {
        File.WriteAllText(Path.Combine(folder, "daml.yaml"), DamlYaml(pin));
        File.WriteAllText(Path.Combine(folder, "multi-package.yaml"), "packages: []\n");
    }

    private static string DamlYaml(CodegenComponentPin pin) => $"name: daml-codegen\nversion: 0.0.1\ncomponents:\n  - {pin.Reference}\n";
}
