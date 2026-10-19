using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Building;

/// <summary>
/// Whether a Daml package's DAR is current, judged from what it is built from rather than from
/// timestamps: the manifest, the exact set of source files with their content, and the DARs it
/// depends on. A source added, deleted or renamed changes the set, so it is noticed as surely as
/// an edit, which a build judged by file times alone misses.
/// </summary>
/// <param name="Fingerprint">Everything the DAR depends on.</param>
/// <param name="SourceFingerprint">The source files alone: their paths and content.</param>
public sealed record PackageBuildState(string Fingerprint, string SourceFingerprint)
{
    private const int Schema = 1;

    // damlc's own output and the build's folders hold .daml files that are no sources of the package.
    private static readonly string[] NotSources = [".daml", "obj", "bin"];

    /// <summary>The state of a package's inputs now.</summary>
    /// <param name="manifestPath">The package's <c>daml.yaml</c>; its content covers the SDK version and build options.</param>
    /// <param name="sourcePath">The package's <c>source</c>: a folder, or a single file.</param>
    /// <param name="dependencyDars">The DARs it depends on; a missing one is part of the state too.</param>
    public static PackageBuildState Compute(string manifestPath, string sourcePath, IEnumerable<string> dependencyDars)
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
        foreach (var dar in dependencyDars.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            all.Append("dependency=").Append(LongPaths.FileExists(dar) ? Hashing.Sha256File(dar) : "missing").Append(' ').Append(dar).Append('\n');
        }
        return new PackageBuildState(Hashing.Sha256Text(all.ToString()), sourceFingerprint);
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
        Read(statePath) is { } recorded && Value(recorded, "fingerprint", Fingerprint) && RecordedDarIsThere(recorded, darPath);

    /// <summary>
    /// Records that the DAR at <paramref name="darPath"/> was just built from this state, unless the
    /// package's inputs aren't this state any more: changed while dpm built, the DAR may hold some of
    /// the old ones, and only a fresh build can tell.
    /// </summary>
    /// <returns>Whether it was recorded.</returns>
    public bool RecordIfUnchanged(string manifestPath, string sourcePath, IEnumerable<string> dependencyDars, string statePath, string darPath)
    {
        if (Compute(manifestPath, sourcePath, dependencyDars).Fingerprint != Fingerprint)
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
