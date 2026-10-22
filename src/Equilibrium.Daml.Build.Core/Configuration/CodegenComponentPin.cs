using System.Text.RegularExpressions;

namespace Equilibrium.Daml.Build.Core.Configuration;

/// <summary>A code generator component pinned to one exact build by its digest.</summary>
/// <param name="Reference">The whole reference, <c>oci://…:tag@sha256:…</c>.</param>
/// <param name="Repository">Registry and repository, e.g. <c>ghcr.io/peacefulstudio/dpm-codegen-cs</c>.</param>
/// <param name="Tag">The tag, e.g. <c>0.6.0</c>; informative only, the digest decides.</param>
/// <param name="Digest">The SHA-256 digest in lowercase hex. It also names the generator's private cache.</param>
public sealed record CodegenComponentPin(string Reference, string Repository, string Tag, string Digest)
{
    // Lowercase hex only: the digest becomes a folder name, and two spellings of one digest must
    // not make two caches.
    private static readonly Regex Shape = new(
        @"^oci://(?<repository>[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)+)" +
        @":(?<tag>[A-Za-z0-9_][A-Za-z0-9._-]{0,127})@sha256:(?<digest>[0-9a-f]{64})\z",
        RegexOptions.CultureInvariant);

    /// <summary>The pin in <paramref name="text"/>, or null unless it names a tag and a SHA-256 digest.</summary>
    public static CodegenComponentPin? TryParse(string? text)
    {
        var match = text is null ? null : Shape.Match(text);
        return match is { Success: true }
            ? new CodegenComponentPin(text!, match.Groups["repository"].Value, match.Groups["tag"].Value, match.Groups["digest"].Value)
            : null;
    }
}
