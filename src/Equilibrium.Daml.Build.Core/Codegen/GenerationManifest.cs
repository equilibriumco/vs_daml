using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>
/// What a generation's folder holds and where it came from, written last when it is published:
/// its presence is what makes the folder the generator's, and its file list is all the build
/// ever compiles or cleans in it. Plain <c>key=value</c> lines; a reader skips keys it doesn't
/// know, so later versions can add to it. The paths in it are written relative to the folder the
/// manifest lives in and read back as full paths, so a generation committed with its project
/// names the right places in any checkout.
/// </summary>
/// <param name="GenerationId">The generation's identity.</param>
/// <param name="Fingerprint">The inputs it was generated from, see <see cref="GenerationFingerprint"/>.</param>
/// <param name="Owner">The canonical path of the project that generated it.</param>
/// <param name="ReferenceKind">Whether it came from a project or a DAR reference.</param>
/// <param name="Reference">The reference's canonical path.</param>
/// <param name="Namespace">The namespace prefix; null for none.</param>
/// <param name="Dar">The DAR it was generated from.</param>
/// <param name="DarSha256">That DAR's SHA-256.</param>
/// <param name="Generator">The generator, as pinned.</param>
/// <param name="Files">Every file the generator wrote, sorted.</param>
public sealed record GenerationManifest(
    string GenerationId,
    string Fingerprint,
    string Owner,
    DamlReferenceKind ReferenceKind,
    string Reference,
    string? Namespace,
    string Dar,
    string DarSha256,
    string Generator,
    IReadOnlyList<GenerationManifestFile> Files)
{
    /// <summary>The manifest's name in a generation's folder.</summary>
    public const string FileName = "daml-generation.manifest";

    /// <summary>The format version this build writes. It still reads version 1, whose paths were full.</summary>
    public const int Schema = 2;

    /// <summary>The package and package ids the DAR names; null when the manifest doesn't record them.</summary>
    public DarProvenance? Package { get; init; }

    /// <summary>The sources a project reference's DAR was built from; null when unknown or for a DAR reference.</summary>
    public SourceProvenance? Source { get; init; }

    /// <summary>Writes the manifest to <paramref name="path"/>.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="folder">
    /// The folder the manifest will live in, which its paths are written relative to; null for the
    /// folder of <paramref name="path"/>. A manifest written in a staging folder and then moved into
    /// place passes the place.
    /// </param>
    public void Write(string path, string? folder = null)
    {
        var home = folder ?? Path.GetDirectoryName(path ?? throw new ArgumentNullException(nameof(path)))!;
        string Relative(string full) => DamlPath.IsFullPath(full) ? DamlPath.Relative(home, full) : full;
        var text = new StringBuilder()
            .Append("schema=").Append(Schema).Append('\n')
            .Append("generationId=").Append(GenerationId).Append('\n')
            .Append("fingerprint=").Append(Fingerprint).Append('\n')
            .Append("owner=").Append(Relative(Owner)).Append('\n')
            .Append("referenceKind=").Append(ReferenceKind == DamlReferenceKind.Dar ? "dar" : "project").Append('\n')
            .Append("reference=").Append(Relative(Reference)).Append('\n')
            .Append("namespace=").Append(Namespace ?? "").Append('\n')
            .Append("dar=").Append(Relative(Dar)).Append('\n')
            .Append("darSha256=").Append(DarSha256).Append('\n')
            .Append("generator=").Append(Generator).Append('\n');
        if (Package is { } package)
        {
            text.Append("darPackage=").Append(package.PackageName).Append('\n')
                .Append("darVersion=").Append(package.PackageVersion).Append('\n')
                .Append("darSdk=").Append(package.SdkVersion ?? "").Append('\n')
                .Append("mainPackageId=").Append(package.MainPackageId).Append('\n');
            foreach (var id in package.DependencyPackageIds)
            {
                text.Append("dependencyPackageId=").Append(id).Append('\n');
            }
            foreach (var module in package.Modules)
            {
                text.Append("darModule=").Append(module).Append('\n');
            }
        }
        if (Source is { } source)
        {
            text.Append("sourceManifest=").Append(Relative(source.ManifestPath)).Append('\n')
                .Append("sourceFingerprint=").Append(source.Fingerprint).Append('\n');
        }
        foreach (var file in Files)
        {
            text.Append("file=").Append(file.Sha256).Append(' ').Append(file.Path).Append('\n');
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>The manifest at <paramref name="path"/>; null when there is none, or it is of another schema or incomplete.</summary>
    /// <param name="path">The manifest.</param>
    /// <param name="folder">The folder its paths are relative to; null for the folder it is in. Reading one still in staging passes its place.</param>
    public static GenerationManifest? Read(string path, string? folder = null)
    {
        if (!File.Exists(path ?? throw new ArgumentNullException(nameof(path))))
        {
            return null;
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<GenerationManifestFile>();
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new List<string>();
        var modules = new List<string>();
        foreach (var line in File.ReadAllText(path, Encoding.UTF8).Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            var (key, value) = (line.Substring(0, equals), line.Substring(equals + 1));
            if (key == "file")
            {
                var space = value.IndexOf(' ');
                if (space <= 0)
                {
                    return null;
                }
                var file = value.Substring(space + 1);
                if (!ValidFilePath(file) || !filePaths.Add(file.Replace('/', '\\')))
                {
                    return null;
                }
                files.Add(new GenerationManifestFile(file, value.Substring(0, space)));
            }
            else if (key == "dependencyPackageId")
            {
                dependencies.Add(value);
            }
            else if (key == "darModule")
            {
                modules.Add(value);
            }
            else
            {
                values[key] = value;
            }
        }
        string? Value(string key) => values.TryGetValue(key, out var value) ? value : null;
        // Version 1 wrote full paths, which resolve to themselves.
        var home = folder ?? Path.GetDirectoryName(path)!;
        string? Full(string? relative) => relative is null ? null : DamlPath.Resolve(home, relative);
        if (Value("schema") is not ("1" or "2")
            || Value("generationId") is not { } id
            || Value("fingerprint") is not { } fingerprint
            || Value("owner") is not { } owner
            || Value("reference") is not { } reference
            || Value("dar") is not { } dar
            || Value("darSha256") is not { } darSha256
            || Value("generator") is not { } generator)
        {
            return null;
        }
        try
        {
            return new GenerationManifest(
                id,
                fingerprint,
                Full(owner)!,
                Value("referenceKind") == "dar" ? DamlReferenceKind.Dar : DamlReferenceKind.Project,
                Full(reference)!,
                Value("namespace") is { Length: > 0 } @namespace ? @namespace : null,
                Full(dar)!,
                darSha256,
                generator,
                files)
            {
                Package = Value("darPackage") is { } package && Value("darVersion") is { } version && Value("mainPackageId") is { } main
                    ? new DarProvenance(package, version, Value("darSdk") is { Length: > 0 } sdk ? sdk : null, main, dependencies) { Modules = modules }
                    : null,
                Source = Value("sourceManifest") is { } sourceManifest && Value("sourceFingerprint") is { } sourceFingerprint
                    ? new SourceProvenance(Full(sourceManifest)!, sourceFingerprint)
                    : null,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that isn't one: the manifest doesn't prove anything.
            return null;
        }
    }

    private static bool ValidFilePath(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path)
        && path.Split(['/', '\\']).All(segment =>
            segment.Length > 0 && segment is not ("." or "..")
            && !segment.EndsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(" ", StringComparison.Ordinal)
            && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
}
