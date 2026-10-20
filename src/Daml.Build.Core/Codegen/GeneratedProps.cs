using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// The props file that tells a project's evaluation which generated files it compiles. Design-time
/// builds never generate, so IntelliSense only sees generated types through this file: a real
/// build writes it, the project imports it while it is evaluated, and Visual Studio evaluates the
/// project again when the file changes, so new types appear without reloading the project.
/// </summary>
public static class GeneratedProps
{
    // The generator names a package's registration after the package ID, which any change to the Daml renews.
    private static readonly Regex PackageIdSuffix = new(@"_[0-9a-f]{64}(?=\.cs$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // How long a build waits for evaluations that hold the old list, about a second in all.
    private const int PublishAttempts = 20;
    private static readonly TimeSpan PublishRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How many files of <paramref name="before"/> are gone from <paramref name="after"/>, the types
    /// IntelliSense may keep offering until the project builds or reloads. A file named after the
    /// package ID that comes back under the new ID is replaced, not gone.
    /// </summary>
    /// <param name="before">The generated files the list had.</param>
    /// <param name="after">The generated files it has now.</param>
    public static int Removed(IEnumerable<string> before, IEnumerable<string> after)
    {
        var kept = new HashSet<string>((after ?? throw new ArgumentNullException(nameof(after))).Select(WithoutPackageId), StringComparer.OrdinalIgnoreCase);
        return (before ?? throw new ArgumentNullException(nameof(before))).Select(WithoutPackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count(f => !kept.Contains(f));
    }

    private static string WithoutPackageId(string path) => PackageIdSuffix.Replace(path, "_");

    /// <summary>
    /// Writes the props for <paramref name="files"/> to <paramref name="path"/> unless it already
    /// says exactly that: an unchanged file mustn't make Visual Studio evaluate the project again.
    /// </summary>
    /// <param name="path">The props file.</param>
    /// <param name="files">The generated files to compile, in order.</param>
    /// <param name="visible">Whether Solution Explorer shows them; files under obj stay hidden.</param>
    /// <returns>Whether the file was written.</returns>
    public static bool Write(string path, IEnumerable<GeneratedFile> files, bool visible)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (files is null)
        {
            throw new ArgumentNullException(nameof(files));
        }
        var text = new StringBuilder("<Project>\n  <ItemGroup>\n");
        foreach (var file in files)
        {
            text.Append("    <Compile Include=\"").Append(Escape(file.Path))
                .Append("\" DamlGenerated=\"true\" DamlGenerationId=\"").Append(Escape(file.GenerationId)).Append('"')
                .Append(visible ? "" : " Visible=\"false\"")
                .Append(" />\n");
        }
        text.Append("  </ItemGroup>\n</Project>\n");
        return WriteIfDifferent(path, text.ToString());
    }

    /// <summary>
    /// Makes an empty props file at <paramref name="path"/> if there is none, so the project imports
    /// it from the next evaluation on and Visual Studio watches it before the first real build.
    /// </summary>
    /// <returns>Whether the file was made.</returns>
    public static bool EnsureExists(string path) =>
        !File.Exists(path ?? throw new ArgumentNullException(nameof(path))) && Publish(path, "<Project />\n", replace: false);

    private static bool WriteIfDifferent(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text)
        {
            return false;
        }
        return Publish(path, text, replace: true);
    }

    // Visual Studio evaluates the project, and so reads this file, while builds write it. Written in
    // place, an evaluation that comes at the wrong moment can't open it and the build fails; so the
    // text goes to a file next to it, which then takes its place in one rename. An evaluation that
    // holds the old file only holds the rename up for a moment.
    private static bool Publish(string path, string text, bool replace)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var next = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        File.WriteAllText(next, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (replace && File.Exists(path))
                    {
                        File.Replace(next, path, null);
                    }
                    else
                    {
                        File.Move(next, path);
                    }
                    return true;
                }
                catch (IOException) when (!replace && File.Exists(path))
                {
                    // Another build made it first; a list is never overwritten by an empty file.
                    return false;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < PublishAttempts)
                {
                    Thread.Sleep(PublishRetryDelay);
                }
            }
        }
        finally
        {
            if (File.Exists(next))
            {
                File.Delete(next);
            }
        }
    }

    /// <summary>
    /// The files a list written by <see cref="Write"/> holds; none when there is no list or it can't
    /// be read, as before a project's first build.
    /// </summary>
    public static IReadOnlyList<GeneratedFile> Read(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        try
        {
            return XDocument.Load(path).Root?.Descendants("Compile")
                .Select(c => new GeneratedFile(Unescape((string?)c.Attribute("Include") ?? ""), Unescape((string?)c.Attribute("DamlGenerationId") ?? "")))
                .Where(f => f.Path.Length > 0)
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }
    }

    // MSBuild's %XX escapes; XML's own are undone by the parser.
    private static string Unescape(string value) =>
        Regex.Replace(value, "%([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    // Escape MSBuild syntax before XML, so XML entities remain intact.
    private static string Escape(string value) =>
        SecurityElement.Escape(value
            .Aggregate(new StringBuilder(), (escaped, c) => c is '%' or '$' or '@' or ';' or '*' or '?' or '\''
                ? escaped.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture))
                : escaped.Append(c))
            .ToString())!;
}
