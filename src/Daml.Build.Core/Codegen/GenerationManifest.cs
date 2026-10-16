using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// What a generation's folder holds and where it came from, written last when it is published:
/// its presence is what makes the folder the generator's, and its file list is all the build
/// ever compiles or cleans in it. Plain <c>key=value</c> lines; a reader skips keys it doesn't
/// know, so later versions can add to it.
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

    /// <summary>The format version this build reads and writes.</summary>
    public const int Schema = 1;

    /// <summary>Writes the manifest to <paramref name="path"/>.</summary>
    public void Write(string path)
    {
        var text = new StringBuilder()
            .Append("schema=").Append(Schema).Append('\n')
            .Append("generationId=").Append(GenerationId).Append('\n')
            .Append("fingerprint=").Append(Fingerprint).Append('\n')
            .Append("owner=").Append(Owner).Append('\n')
            .Append("referenceKind=").Append(ReferenceKind == DamlReferenceKind.Dar ? "dar" : "project").Append('\n')
            .Append("reference=").Append(Reference).Append('\n')
            .Append("namespace=").Append(Namespace ?? "").Append('\n')
            .Append("dar=").Append(Dar).Append('\n')
            .Append("darSha256=").Append(DarSha256).Append('\n')
            .Append("generator=").Append(Generator).Append('\n');
        foreach (var file in Files)
        {
            text.Append("file=").Append(file.Sha256).Append(' ').Append(file.Path).Append('\n');
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>The manifest at <paramref name="path"/>; null when there is none, or it is of another schema or incomplete.</summary>
    public static GenerationManifest? Read(string path)
    {
        if (!File.Exists(path ?? throw new ArgumentNullException(nameof(path))))
        {
            return null;
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<GenerationManifestFile>();
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
                files.Add(new GenerationManifestFile(value.Substring(space + 1), value.Substring(0, space)));
            }
            else
            {
                values[key] = value;
            }
        }
        string? Value(string key) => values.TryGetValue(key, out var value) ? value : null;
        if (Value("schema") != Schema.ToString(System.Globalization.CultureInfo.InvariantCulture)
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
        return new GenerationManifest(
            id,
            fingerprint,
            owner,
            Value("referenceKind") == "dar" ? DamlReferenceKind.Dar : DamlReferenceKind.Project,
            reference,
            Value("namespace") is { Length: > 0 } @namespace ? @namespace : null,
            dar,
            darSha256,
            generator,
            files);
    }
}
