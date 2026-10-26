using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>
/// The identity of a reference's generated code. It names the folder the code is kept in, so
/// it must come out the same in every build and on every host for the same reference.
/// </summary>
internal static class GenerationIdentity
{
    private const int LabelLength = 32;

    /// <summary>
    /// The first 64 bits, as 16 lowercase hex digits, of a SHA-256 over the reference's kind, its
    /// path as seen from the project in upper case (Windows paths ignore case), the namespace (C#
    /// doesn't) and the generator's digest. The relative path makes the id the same in every
    /// checkout of a repository, which committed bindings depend on; a reference on another drive
    /// has no relative path and counts with its full one. The contract version of the generated
    /// code is deliberately left out: a new contract regenerates the same folder rather than
    /// starting another.
    /// </summary>
    /// <param name="kind">Project or DAR.</param>
    /// <param name="canonicalPath">The reference's canonical full path.</param>
    /// <param name="projectDirectory">The folder of the project that generates the code.</param>
    /// <param name="namespace">The namespace prefix, or null for none.</param>
    /// <param name="generatorDigest">The generator's SHA-256 digest in lowercase hex.</param>
    public static string Id(DamlReferenceKind kind, string canonicalPath, string projectDirectory, string? @namespace, string generatorDigest)
    {
        if (canonicalPath is null)
        {
            throw new ArgumentNullException(nameof(canonicalPath));
        }
        if (projectDirectory is null)
        {
            throw new ArgumentNullException(nameof(projectDirectory));
        }
        if (generatorDigest is null)
        {
            throw new ArgumentNullException(nameof(generatorDigest));
        }
        var text = string.Join(
            "\n",
            "daml-generation-id/2",
            kind == DamlReferenceKind.Dar ? "dar" : "project",
            DamlPath.Relative(projectDirectory, canonicalPath).ToUpperInvariant(),
            @namespace ?? "",
            generatorDigest);
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        }
        var id = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            id.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return id.ToString();
    }

    /// <summary>
    /// <c>&lt;label&gt;-&lt;id&gt;</c>: the label is the reference's file name without its
    /// extension, kept to letters, digits, <c>.</c>, <c>_</c> and <c>-</c> and 32 characters, so a
    /// person can tell the folders apart. Only the id identifies the folder.
    /// </summary>
    public static string FolderName(string canonicalPath, string generationId)
    {
        var name = Path.GetFileNameWithoutExtension(canonicalPath ?? throw new ArgumentNullException(nameof(canonicalPath)));
        var label = new StringBuilder(Math.Min(name.Length, LabelLength));
        foreach (var c in name)
        {
            if (label.Length == LabelLength)
            {
                break;
            }
            label.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' ? c : '_');
        }
        return (label.Length == 0 ? "reference" : label.ToString()) + "-" + generationId;
    }

    /// <summary>The generation id at the end of a <see cref="FolderName"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="folderName"/> doesn't end in one.</exception>
    public static string IdOfFolder(string folderName) =>
        TryIdOfFolder(folderName ?? throw new ArgumentNullException(nameof(folderName)))
        ?? throw new ArgumentException($"{folderName} isn't the name of a generation's folder.", nameof(folderName));

    /// <summary>The generation id at the end of a <see cref="FolderName"/>; null when the name doesn't end in one.</summary>
    public static string? TryIdOfFolder(string folderName)
    {
        var dash = (folderName ?? throw new ArgumentNullException(nameof(folderName))).LastIndexOf('-');
        var id = dash < 0 ? "" : folderName.Substring(dash + 1);
        return IsId(id) ? id : null;
    }

    /// <summary>Whether <paramref name="text"/> has the form of a generation id: 16 lowercase hex digits.</summary>
    public static bool IsId(string text) =>
        text is { Length: 16 } && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
