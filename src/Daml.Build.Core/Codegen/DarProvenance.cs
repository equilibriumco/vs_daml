using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// What a DAR says about itself in its manifest: the package, the SDK that built it, and the
/// ids of the packages it holds. Navigation between the generated C# and the Daml sources goes
/// by these ids, never by names, since two DARs can share a name.
/// </summary>
/// <param name="PackageName">The package's name, e.g. <c>splice-api-token-holding-v2</c>.</param>
/// <param name="PackageVersion">The package's version, e.g. <c>1.0.0</c>.</param>
/// <param name="SdkVersion">The SDK that built it; null when the manifest doesn't say.</param>
/// <param name="MainPackageId">The id of the package the DAR is for.</param>
/// <param name="DependencyPackageIds">The ids of the other packages in it, sorted.</param>
public sealed record DarProvenance(
    string PackageName,
    string PackageVersion,
    string? SdkVersion,
    string MainPackageId,
    IReadOnlyList<string> DependencyPackageIds)
{
    /// <summary>The main package's original LF module names; empty for older metadata without a name table.</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];

    private const string ManifestEntry = "META-INF/MANIFEST.MF";
    private const int PackageIdLength = 64;

    /// <summary>Reads package metadata and available module names, validating the main DALF payload when present.</summary>
    /// <exception cref="InvalidDataException">The DAR, its manifest or its main DALF payload is invalid.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    public static DarProvenance Read(string darPath) => Read(darPath, includeModules: true);

    /// <summary>Reads package metadata from the DAR manifest without opening or validating the DALF payload.</summary>
    public static DarProvenance ReadManifest(string darPath) => Read(darPath, includeModules: false);

    private static DarProvenance Read(string darPath, bool includeModules)
    {
        using var stream = File.OpenRead(LongPaths.Safe(darPath ?? throw new ArgumentNullException(nameof(darPath))));
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{darPath} isn't a DAR: it isn't a zip archive.", ex);
        }
        using var ownedZip = zip;
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException($"{darPath} isn't a DAR: it has no {ManifestEntry}.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var text = reader.ReadToEnd();

        var fields = Fields(text);
        string? Field(string name) => fields.TryGetValue(name, out var value) ? value : null;
        var name = Field("Name");
        var dash = name?.LastIndexOf('-') ?? -1;
        if (name is null || dash <= 0 || dash == name.Length - 1)
        {
            throw new InvalidDataException($"The manifest of {darPath} doesn't name its package and version.");
        }
        var main = PackageId(Field("Main-Dalf"))
            ?? throw new InvalidDataException($"The manifest of {darPath} doesn't name its main package.");
        var dependencies = (Field("Dalfs") ?? "")
            .Split(',')
            .Select(dalf => PackageId(dalf.Trim()))
            .OfType<string>()
            .Where(id => id != main)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<string> modules = [];
        if (includeModules && Field("Main-Dalf") is { } dalf && zip.GetEntry(dalf) is { } mainEntry)
        {
            using var mainStream = mainEntry.Open();
            modules = DalfModules.Read(mainStream, main);
        }
        return new DarProvenance(name.Substring(0, dash), name.Substring(dash + 1), Field("Sdk-Version"), main, dependencies) { Modules = modules };
    }

    /// <inheritdoc />
    public bool Equals(DarProvenance? other) =>
        other is not null
        && PackageName == other.PackageName
        && PackageVersion == other.PackageVersion
        && SdkVersion == other.SdkVersion
        && MainPackageId == other.MainPackageId
        && DependencyPackageIds.SequenceEqual(other.DependencyPackageIds, StringComparer.Ordinal)
        && Modules.SequenceEqual(other.Modules, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(MainPackageId);

    // A JAR manifest: "Name: value" lines wrapped at 72 bytes, each continuation starting with one space.
    private static Dictionary<string, string> Fields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? last = null;
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith(" ", StringComparison.Ordinal) && last is not null)
            {
                fields[last] += line.Substring(1);
                continue;
            }
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                last = line.Substring(0, colon);
                fields[last] = line.Substring(colon + 2);
            }
        }
        return fields;
    }

    // dalf files end in "-<package id>.dalf", whatever comes before.
    private static string? PackageId(string? dalf)
    {
        if (dalf is null || !dalf.EndsWith(".dalf", StringComparison.Ordinal))
        {
            return null;
        }
        var stem = dalf.Substring(0, dalf.Length - ".dalf".Length);
        if (stem.Length <= PackageIdLength || stem[stem.Length - PackageIdLength - 1] != '-')
        {
            return null;
        }
        var id = stem.Substring(stem.Length - PackageIdLength);
        return id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? id : null;
    }
}
