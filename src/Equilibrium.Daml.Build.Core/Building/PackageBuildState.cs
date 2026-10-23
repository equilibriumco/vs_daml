using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.Build.Core.Tools;

namespace Equilibrium.Daml.Build.Core.Building;

/// <summary>
/// Whether a Daml package's DAR is current, judged from what it is built from rather than from
/// timestamps: the package and multi-package manifests, the source files, and the DARs/DALFs it
/// depends on. A source added, deleted or renamed changes the set, so it is noticed as surely as
/// an edit, which a build judged by file times alone misses.
/// </summary>
/// <param name="Fingerprint">Hash of the manifest, source and dependency inputs tracked by the build.</param>
/// <param name="SourceFingerprint">The source files alone: their paths and content.</param>
public sealed record PackageBuildState(string Fingerprint, string SourceFingerprint)
{
    /// <summary>False when mutable or unpinned tool inputs require DPM to resolve them again.</summary>
    public bool CanReuse { get; init; } = true;

    private const int Schema = 1;

    // DPM's build resolver requires strict SemVer, including prerelease/build identifiers.
    private static readonly Regex PinnedSdk = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // damlc's own output and the build's folders hold .daml files that are no sources of the package.
    private static readonly string[] NotSources = [".daml", "obj", "bin"];

    /// <summary>The state of a package's inputs now.</summary>
    /// <param name="manifestPath">The package's <c>daml.yaml</c>; its content covers the SDK version and build options.</param>
    /// <param name="sourcePath">The package's <c>source</c>: a folder, or a single file.</param>
    /// <param name="dependencyDars">Local dependency archives; a missing one is part of the state too.</param>
    public static PackageBuildState Compute(string manifestPath, string sourcePath, IEnumerable<string> dependencyDars) =>
        Compute(manifestPath, sourcePath, dependencyDars, Environment.GetEnvironmentVariable, null);

    /// <summary>Computes state with the environment and DPM executable selected by the caller.</summary>
    public static PackageBuildState Compute(string manifestPath, string sourcePath, IEnumerable<string> dependencyDars,
        Func<string, string?> environment, string? dpmPath)
    {
        if (manifestPath is null)
        {
            throw new ArgumentNullException(nameof(manifestPath));
        }
        if (dependencyDars is null)
        {
            throw new ArgumentNullException(nameof(dependencyDars));
        }
        var folder = SourceFolder(sourcePath);
        var sources = new StringBuilder("daml-sources/1\n");
        foreach (var file in SourceFiles(sourcePath))
        {
            sources.Append(Hashing.Sha256File(file)).Append(' ').Append(file.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar)).Append('\n');
        }
        var sourceFingerprint = Hashing.Sha256Text(sources.ToString());

        var all = new StringBuilder("daml-build-state/1\n")
            .Append("manifest=").Append(Hashing.Sha256File(manifestPath)).Append('\n')
            .Append("sources=").Append(sourceFingerprint).Append('\n');
        var reader = new YamlManifestReader(new PhysicalFileSystem(), environment);
        var package = reader.ReadPackage(manifestPath).Value;
        var multiPackage = FindMultiPackageManifest(manifestPath, null, environment);
        if (multiPackage is not null)
        {
            all.Append("multi-package=").Append(File.Exists(multiPackage) ? Hashing.Sha256File(multiPackage) : "missing").Append(' ').Append(multiPackage).Append('\n');
        }
        all.Append("expanded=").Append(Hashing.Sha256Text(PackageEnvironment.Expand(File.ReadAllText(manifestPath), environment))).Append('\n');
        foreach (var variable in new[] { "DPM_SDK_VERSION", "DPM_MULTI_PACKAGE", "DPM_HOME", "DPM_REGISTRY", "DPM_EDITION", "DAML_PROJECT", "DAML_PACKAGE", "DPM_COMPONENTS" })
        {
            all.Append(variable).Append('=').Append(environment(variable)).Append('\n');
        }
        var tool = DpmLocation.Find(dpmPath, Path.GetDirectoryName(manifestPath)!, environment, new PhysicalFileSystem(), new List<ConfigurationError>());
        if (tool is not null)
        {
            all.Append("dpm=").Append(tool).Append(' ').Append(Hashing.Sha256File(tool)).Append('\n');
        }
        var multi = multiPackage is null ? null : reader.ReadMultiPackage(multiPackage).Value;
        var sdk = environment("DPM_SDK_VERSION") ?? package?.SdkVersion ?? multi?.SdkVersion;
        var home = DpmLocation.HomeDirectory(Path.GetDirectoryName(manifestPath)!, environment);
        // Edition may come from dpm-config.yaml; track each edition without duplicating its parser.
        var configs = home is null ? Enumerable.Empty<string>() : new[] { Path.Combine(home, "dpm-config.yaml") }.Concat(
            new[] { "open-source", "enterprise", "private", environment("DPM_EDITION") }
                .Where(edition => !string.IsNullOrWhiteSpace(edition)).Distinct(StringComparer.Ordinal)
                .SelectMany(edition => SdkManifests(home, edition!, sdk)));
        foreach (var config in configs)
        {
            all.Append("tool-config=").Append(config).Append(' ').Append(File.Exists(config) ? Hashing.Sha256File(config) : "missing").Append('\n');
        }
        foreach (var dar in dependencyDars.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            all.Append("dependency=").Append(LongPaths.FileExists(dar) ? Hashing.Sha256File(dar) : "missing").Append(' ').Append(dar).Append('\n');
        }
        return new PackageBuildState(Hashing.Sha256Text(all.ToString()), sourceFingerprint)
        {
            CanReuse = sdk is not null && PinnedSdk.IsMatch(sdk)
                && package is not null && package.Components.Count == 0
                && (multi is null || multi.Components.Count == 0)
                && package.Dependencies.Concat(package.DataDependencies).All(d => !d.Contains("://") && !d.StartsWith("@", StringComparison.Ordinal)),
        };
    }

    private static IEnumerable<string> SdkManifests(string home, string edition, string? sdk)
    {
        var folder = Path.Combine(home, "cache", "sdk", edition);
        // DPM compares SemVer precedence, which ignores build metadata when selecting an installed SDK.
        var version = (sdk ?? "").Split('+')[0];
        yield return Path.Combine(folder, version + ".yaml");
        if (sdk is not null && PinnedSdk.IsMatch(sdk) && Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, version + "+*.yaml").OrderBy(p => p, StringComparer.Ordinal))
            {
                yield return path;
            }
        }
    }

    /// <summary>The nearest multi-package manifest in the package directory or its ancestors, or null.</summary>
    /// <param name="manifestPath">The package manifest's path.</param>
    /// <param name="fileExists">Tests whether a file exists; null uses the physical filesystem.</param>
    public static string? FindMultiPackageManifest(string manifestPath, Func<string, bool>? fileExists = null) =>
        FindMultiPackageManifest(manifestPath, fileExists, Environment.GetEnvironmentVariable);

    /// <summary>Finds the multi-package manifest, honoring DPM_MULTI_PACKAGE's directory override.</summary>
    public static string? FindMultiPackageManifest(string manifestPath, Func<string, bool>? fileExists, Func<string, string?> environment)
    {
        fileExists ??= File.Exists;
        if (environment("DPM_MULTI_PACKAGE") is { } directory)
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, directory, "multi-package.yaml"));
        }
        for (var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath)); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var candidate = Path.Combine(folder, "multi-package.yaml");
            if (fileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// The source files damlc compiles: every <c>.daml</c> file under the source folder, or, for a
    /// single source file, under its folder, whose files its imports come from. Sorted, full paths.
    /// </summary>
    public static IReadOnlyList<string> SourceFiles(string sourcePath)
    {
        var folder = SourceFolder(sourcePath);
        if (!Directory.Exists(folder))
        {
            return [];
        }
        return LongPaths.RelativeFiles(folder)
            .Where(f => f.EndsWith(".daml", StringComparison.OrdinalIgnoreCase)
                && !f.Split(Path.DirectorySeparatorChar).Take(f.Count(c => c == Path.DirectorySeparatorChar))
                    .Any(segment => NotSources.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => Path.Combine(folder, f))
            .ToList();
    }

    /// <summary>
    /// Whether the build recorded at <paramref name="statePath"/> was built from this state, and its
    /// DAR is still the one it built.
    /// </summary>
    public bool IsRecordedIn(string statePath, string darPath) =>
        CanReuse && Read(statePath) is { } recorded && Value(recorded, "fingerprint", Fingerprint) && RecordedDarIsThere(recorded, darPath);

    /// <summary>
    /// Records that the DAR at <paramref name="darPath"/> was just built from this state, unless the
    /// package's inputs aren't this state any more: changed while dpm built, the DAR may hold some of
    /// the old ones, and only a fresh build can tell. The inputs are read with <c>environment</c>, which
    /// has to be the one this state was computed with; this process's by default.
    /// </summary>
    /// <returns>Whether it was recorded.</returns>
    public bool RecordIfUnchanged(string manifestPath, string sourcePath, IEnumerable<string> dependencyDars, string statePath, string darPath, string? dpmPath = null,
        Func<string, string?>? environment = null)
    {
        if (Compute(manifestPath, sourcePath, dependencyDars, environment ?? Environment.GetEnvironmentVariable, dpmPath).Fingerprint != Fingerprint)
        {
            return false;
        }
        Record(statePath, darPath);
        return true;
    }

    /// <summary>Records that the DAR at <paramref name="darPath"/> was just built from this state.</summary>
    public void Record(string statePath, string darPath)
    {
        var dar = new FileInfo(LongPaths.Safe(darPath ?? throw new ArgumentNullException(nameof(darPath))));
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(
            statePath,
            new StringBuilder()
                .Append("schema=").Append(Schema).Append('\n')
                .Append("fingerprint=").Append(Fingerprint).Append('\n')
                .Append("sources=").Append(SourceFingerprint).Append('\n')
                .Append("dar=").Append(darPath).Append('\n')
                .Append("darLength=").Append(dar.Length.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("darTime=").Append(dar.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// The fingerprint of the sources the DAR at <paramref name="darPath"/> was built from, as its
    /// build recorded it; nothing is hashed. Null when no build recorded it, or the DAR there isn't
    /// the one it built: sources that didn't make the DAR must never be named as its own.
    /// </summary>
    public static string? RecordedSourceFingerprint(string statePath, string darPath) =>
        Read(statePath) is { } recorded && RecordedDarIsThere(recorded, darPath) && recorded.TryGetValue("sources", out var sources) ? sources : null;

    /// <summary>Removes the package's own outputs: its DAR and the record of what it was built from.</summary>
    /// <exception cref="IOException">One of them couldn't be deleted.</exception>
    public static void RemoveOutputs(string statePath, string darPath)
    {
        foreach (var file in new[] { darPath, statePath })
        {
            var path = LongPaths.Safe(file ?? throw new ArgumentNullException(nameof(darPath)));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static bool RecordedDarIsThere(Dictionary<string, string> recorded, string darPath)
    {
        if (!Value(recorded, "dar", darPath) || !LongPaths.FileExists(darPath))
        {
            return false;
        }
        var dar = new FileInfo(LongPaths.Safe(darPath));
        return Value(recorded, "darLength", dar.Length.ToString(CultureInfo.InvariantCulture))
            && Value(recorded, "darTime", dar.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static string SourceFolder(string sourcePath) =>
        Directory.Exists(sourcePath ?? throw new ArgumentNullException(nameof(sourcePath))) ? sourcePath : Path.GetDirectoryName(sourcePath)!;

    private static Dictionary<string, string>? Read(string statePath)
    {
        if (!File.Exists(statePath ?? throw new ArgumentNullException(nameof(statePath))))
        {
            return null;
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(statePath))
        {
            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                values[line.Substring(0, equals)] = line.Substring(equals + 1);
            }
        }
        return values.TryGetValue("schema", out var schema) && schema == Schema.ToString(CultureInfo.InvariantCulture) ? values : null;
    }

    private static bool Value(Dictionary<string, string> recorded, string key, string expected) =>
        recorded.TryGetValue(key, out var value) && string.Equals(value, expected, StringComparison.Ordinal);
}
