using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Manifests;

/// <summary>What building a package needs from its <c>daml.yaml</c>, checked the way dpm would need it.</summary>
/// <param name="ManifestPath">Full path of the manifest.</param>
/// <param name="PackageRoot">The folder dpm builds in.</param>
/// <param name="Name"><c>name</c>.</param>
/// <param name="Version"><c>version</c>.</param>
/// <param name="SdkVersion"><c>sdk-version</c>, or null when the manifest leaves it to dpm.</param>
/// <param name="Components"><c>components</c> as written.</param>
/// <param name="Source">The sources, which exist.</param>
/// <param name="Dependencies"><c>dependencies</c>.</param>
/// <param name="DataDependencies"><c>data-dependencies</c>, including local DALFs.</param>
/// <param name="BuildOptions"><c>build-options</c> as written.</param>
/// <param name="DarPath">Where the build writes the DAR: the output named in <c>build-options</c>, or <c>.daml\dist\&lt;name&gt;-&lt;version&gt;.dar</c>.</param>
public sealed record DamlPackageBuild(
    string ManifestPath,
    string PackageRoot,
    string Name,
    string Version,
    string? SdkVersion,
    IReadOnlyList<string> Components,
    DamlSource Source,
    IReadOnlyList<DamlDependency> Dependencies,
    IReadOnlyList<DamlDependency> DataDependencies,
    IReadOnlyList<string> BuildOptions,
    string DarPath)
{
    /// <summary>Local component names and paths passed through to DPM.</summary>
    public IReadOnlyDictionary<string, string> LocalComponents { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Reads and checks the manifest at <paramref name="manifestPath"/>. Anything dpm would stop on
    /// (a missing name, version or source, sources that aren't there, an output option it couldn't
    /// follow) is an error, and so is any field the reader had to skip: building from a half-read
    /// manifest would build something other than dpm does.
    /// </summary>
    /// <returns>The package, or null with the errors in the diagnostics.</returns>
    public static ManifestResult<DamlPackageBuild> Read(IManifestReader reader, IDamlFileSystem files, string manifestPath)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }
        if (files is null)
        {
            throw new ArgumentNullException(nameof(files));
        }
        var read = reader.ReadPackage(manifestPath);
        if (read.Value is not { } manifest)
        {
            return new ManifestResult<DamlPackageBuild>(null, read.Diagnostics);
        }

        var diagnostics = read.Diagnostics.ToList();
        void Error(string message) => diagnostics.Add(new ManifestDiagnostic(manifest.ManifestPath, null, null, ManifestSeverity.Error, message));

        var name = Required(manifest.Name, "name", Error);
        var version = Required(manifest.Version, "version", Error);
        var source = Required(manifest.Source, "source", Error) is { } sourceText ? ExistingSource(manifest.PackageRoot, sourceText, files, Error) : null;
        var output = Output(manifest.BuildOptions, Error);

        if (diagnostics.Any(d => d.Severity == ManifestSeverity.Error) || name is null || version is null || source is null)
        {
            return new ManifestResult<DamlPackageBuild>(null, diagnostics);
        }
        var package = new DamlPackageBuild(
            manifest.ManifestPath,
            manifest.PackageRoot,
            name,
            version,
            manifest.SdkVersion,
            manifest.Components,
            source,
            manifest.Dependencies.Select(d => new DamlDependency(d, DamlDependency.IsLocalArchive(d) ? DamlPath.Resolve(manifest.PackageRoot, d) : null)).ToList(),
            manifest.DataDependencies.Select(d => new DamlDependency(d, DamlDependency.IsLocalArchive(d) ? DamlPath.Resolve(manifest.PackageRoot, d) : null)).ToList(),
            manifest.BuildOptions,
            DamlPath.Resolve(manifest.PackageRoot, output ?? Path.Combine(".daml", "dist", $"{name}-{version}.dar")))
        {
            LocalComponents = manifest.LocalComponents,
        };
        return new ManifestResult<DamlPackageBuild>(package, diagnostics);
    }

    private static string? Required(string? value, string key, Action<string> error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error($"daml.yaml has no '{key}'; dpm needs one to build the package.");
            return null;
        }
        return value!.Trim();
    }

    private static DamlSource? ExistingSource(string packageRoot, string text, IDamlFileSystem files, Action<string> error)
    {
        var path = DamlPath.Resolve(packageRoot, text);
        if (files.DirectoryExists(path))
        {
            return new DamlSource(DamlSourceKind.Folder, path);
        }
        if (files.FileExists(path))
        {
            return new DamlSource(DamlSourceKind.File, path);
        }
        error($"The source '{text}' in daml.yaml doesn't exist ({path}).");
        return null;
    }

    // damlc's output option, in the spellings its option parser takes from separate entries:
    // "-o FILE", "--output FILE" and "--output=FILE". One entry holding the option and the file
    // reaches damlc as a single argument it doesn't know.
    private static string? Output(IReadOnlyList<string> options, Action<string> error)
    {
        string? output = null;
        var count = 0;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i].Trim();
            string? value;
            if (option is "-o" or "--output")
            {
                if (i + 1 >= options.Count)
                {
                    error($"'{option}' in build-options has no file after it.");
                    return null;
                }
                value = options[++i].Trim();
            }
            else if (option.StartsWith("--output=", StringComparison.Ordinal))
            {
                value = option.Substring("--output=".Length);
            }
            else if (option.StartsWith("-o ", StringComparison.Ordinal) || option.StartsWith("--output ", StringComparison.Ordinal))
            {
                error($"build-options entry '{option}' has to be two entries: the option, then the file.");
                return null;
            }
            else
            {
                continue;
            }
            if (value.Length == 0)
            {
                error($"'{option}' in build-options has no file after it.");
                return null;
            }
            output = value;
            count++;
        }
        if (count > 1)
        {
            error("build-options name the output more than once; keep one.");
            return null;
        }
        return output;
    }
}
