using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Tests;

/// <summary>
/// The kinds fixture published as generations in a throwaway folder, the way a build lays them
/// out: one folder per generation under a project's generation root, with a manifest listing every
/// file and its hash. The namespace prefix and the package are rewritten throughout the code, so
/// one fixture stands for other references, projects and packages.
/// </summary>
internal sealed class GenerationsOnDisk : IDisposable
{
    private const string FixturePrefix = "Fixture";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "daml-generations-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>Publishes the fixture as a generation of <paramref name="project"/> and returns its files.</summary>
    /// <param name="project">The project folder under <see cref="Root"/>.</param>
    /// <param name="namespace">The reference's namespace prefix; null for none.</param>
    /// <param name="package">The package the code states.</param>
    /// <param name="manifestPackage">The package the manifest records, when it should disagree with the code.</param>
    /// <param name="recordPackage">Whether the manifest records the DAR's package at all.</param>
    /// <param name="framework">The target framework, whose generation root it goes in.</param>
    /// <param name="id">The generation's identity, which names its folder; by default one per project, prefix and package.</param>
    public List<(string Path, string Text)> Publish(string project, string? @namespace = FixturePrefix, string package = GeneratedCode.KindsPackage, string? manifestPackage = null, bool recordPackage = true, string framework = "net10.0", string? id = null)
    {
        id ??= Hashing.Sha256Text(project + "|" + @namespace + "|" + package).Substring(0, 16);
        var folder = Path.Combine(Root, project, @"obj\Debug", framework, "Daml", "Contracts-" + id);
        // Publishing replaces the whole generation, as a build does.
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
        var written = new List<(string Path, string Text)>();
        var entries = new List<GenerationManifestFile>();
        foreach (var (fixturePath, fixtureText) in GeneratedCode.Files(GeneratedCode.Kinds))
        {
            var relative = fixturePath.Substring(GeneratedCode.Kinds.Length + 1).Replace(GeneratedCode.KindsPackage, package);
            relative = @namespace is null ? relative.Substring(FixturePrefix.Length + 1) : @namespace.Replace('.', '\\') + relative.Substring(FixturePrefix.Length);
            var text = fixtureText
                .Replace(FixturePrefix + ".Kinds", @namespace is null ? "Kinds" : @namespace + ".Kinds")
                .Replace(GeneratedCode.KindsPackage, package);
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            written.Add((path, text));
            entries.Add(new GenerationManifestFile(relative, Hashing.Sha256File(path)));
        }
        new GenerationManifest(
            id, "f00d", Path.Combine(Root, project, project + ".csproj"), DamlReferenceKind.Project, Path.Combine(Root, @"Contracts\Contracts.msbuildproj"),
            @namespace, Path.Combine(Root, @"Contracts\.daml\dist\kinds-1.0.0.dar"), "da7a", "oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:9f04",
            entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList())
        {
            Package = recordPackage ? new DarProvenance("kinds", "1.0.0", "3.6.0", manifestPackage ?? package, []) { Modules = ["Kinds.Asset", "Kinds.Holding", "Kinds.Types"] } : null,
            Source = new SourceProvenance(Path.Combine(Root, @"Contracts\daml.yaml"), "50c"),
        }.Write(Path.Combine(folder, GenerationManifest.FileName));
        return written;
    }
}
