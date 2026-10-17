using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// The props file that tells a project's evaluation which generated files it compiles. Design-time
/// builds never generate, so IntelliSense only sees generated types through this file: a real
/// build writes it, the project imports it while it is evaluated, and Visual Studio evaluates the
/// project again when the file changes, so new types appear without reloading the project.
/// </summary>
public static class GeneratedProps
{
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
        !File.Exists(path ?? throw new ArgumentNullException(nameof(path))) && WriteIfDifferent(path, "<Project />\n");

    private static bool WriteIfDifferent(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text)
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    // What MSBuild would read as a property, an item list, metadata or a wildcard first, then XML;
    // the other way round, MSBuild's escaping would break the XML entities.
    private static string Escape(string value) =>
        SecurityElement.Escape(value
            .Aggregate(new StringBuilder(), (escaped, c) => c is '%' or '$' or '@' or ';' or '*' or '?' or '\''
                ? escaped.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture))
                : escaped.Append(c))
            .ToString())!;
}
