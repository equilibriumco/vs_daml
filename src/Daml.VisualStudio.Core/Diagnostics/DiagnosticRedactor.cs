using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Daml.VisualStudio.Core.Diagnostics;

/// <summary>A folder whose paths a <see cref="DiagnosticRedactor"/> shortens to a placeholder.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Placeholder">What stands for it, such as <c>${HOME}</c>.</param>
/// <param name="KeepRest">
/// Keep the path below the folder. Only for folders holding tools and caches, whose layout
/// (versions, component names) helps a bug report and says nothing about the user.
/// </param>
public sealed record RedactedFolder(string Path, string Placeholder, bool KeepRest);

/// <summary>
/// Masks what shouldn't leave the machine in a diagnostic export: paths (only a placeholder for
/// a known folder and the file name stay), the user and machine names, source code quoted by the
/// compiler or in protocol messages, script results (assertion text, transactions, parties,
/// contract IDs) and credentials. Line-oriented rules work on the shapes damlc and dpm print;
/// anything else is caught only by the generic path, ID and secret rules.
/// </summary>
public sealed class DiagnosticRedactor
{
    private const string Code = "<redacted code>";
    private const string Result = "<redacted result>";
    private const string ResultBlock = "<redacted script result data>";

    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // A drive (also with a URI-escaped colon) or UNC root, then segments. A segment may hold
    // single spaces only when a separator follows it, so "C:\x\a b.txt was" stops at "a".
    private static readonly Regex AbsolutePath = new(
        @"(?<![\w$%])(?:[A-Za-z](?::|%3[Aa])|\\\\[^\\/:*?""'<>|\s]+)(?:[\\/](?:[^\\/:*?""'<>|\s]+(?: [^\\/:*?""'<>|\s]+)*(?=[\\/])|[^\\/:*?""'<>|\s]+))+[\\/]?",
        Options);

    // LogFileSink stamps each entry, including individual compiler output lines.
    private static readonly Regex LogPrefix = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} (?:trce|dbug|info|warn|fail) \[[^\]\r\n]+\] ", Options);

    // GHC's source excerpt ("12 |   code" and its "   |" frame) and context lines
    // ("In the expression: code", "In an equation for ‘f’: code"). These can also
    // occur inside a diagnostic flattened by BuildDiagnostic.ToOutputLine.
    private static readonly Regex ExcerptLine = new(@"(?<!\S)\d*\s*\|.*$", Options);
    private static readonly Regex ContextLine = new(@"((?<!\S)(?:•\s*)?In (?:the|an|a) [^:]*:)\s*\S.*$", Options);

    private static readonly Regex FailureStatus = new(@"(Failed with status: [A-Z_]+(?:/[\w.]+)?)(?::.*)?$", Options);
    private static readonly Regex ResultHeader = new(
        @"^(\s*(?:Partial transaction|Committed transactions|Active contracts|Return value|Sub-transactions|Disclosures|Trace)\s*:)\s*(.*)$",
        Options);

    private static readonly Regex Payload = new(@"""(text|newText|contents|value)""\s*:\s*""((?:[^""\\]|\\.)*)""", Options);
    private static readonly Regex Party = new(@"[\w.-]+::1220[0-9a-f]{64}\b", Options);
    private static readonly Regex QuotedParty = new(@"'[A-Za-z][\w.-]*'", Options);
    private static readonly Regex ContractId = new(@"\b00[0-9a-f]{64,}\b", Options);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", Options);

    private readonly List<(RedactedFolder Folder, string Normalized)> _folders;
    private readonly List<(Regex Pattern, string Placeholder)> _names;

    /// <summary>Creates a redactor for the given folders and names; where folders nest, the longest wins.</summary>
    public DiagnosticRedactor(IEnumerable<RedactedFolder> folders, IEnumerable<(string Name, string Placeholder)> names)
    {
        _folders = (folders ?? throw new ArgumentNullException(nameof(folders)))
            .Where(f => !string.IsNullOrWhiteSpace(f.Path))
            .Select(f => (f, Normalize(f.Path)))
            .OrderByDescending(f => f.Item2.Length)
            .ToList();
        // Very short names would mask ordinary words.
        _names = (names ?? throw new ArgumentNullException(nameof(names)))
            .Where(n => n.Name is { Length: >= 3 })
            .Select(n => (new Regex($@"(?<![\w-]){Regex.Escape(n.Name)}(?![\w-])", Options | RegexOptions.IgnoreCase), n.Placeholder))
            .ToList();
    }

    /// <summary>
    /// A redactor for this user and machine: dpm's home and the extension's data keep their
    /// layout; the temp folder, the local and roaming application data, the user profile and
    /// <paramref name="workspaceRoots"/> become placeholders.
    /// </summary>
    public static DiagnosticRedactor ForCurrentUser(IEnumerable<string> workspaceRoots)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dpmHome = Environment.GetEnvironmentVariable("DPM_HOME");
        var folders = new List<RedactedFolder>
        {
            new(Path.Combine(roaming, "dpm"), "${DPM}", KeepRest: true),
            new(Path.Combine(local, "DamlVisualStudio"), "${EXTENSIONDATA}", KeepRest: true),
            new(Path.GetTempPath(), "${TEMP}", KeepRest: false),
            new(local, "${LOCALAPPDATA}", KeepRest: false),
            new(roaming, "${APPDATA}", KeepRest: false),
            new(home, "${HOME}", KeepRest: false),
        };
        if (!string.IsNullOrWhiteSpace(dpmHome))
        {
            folders.Add(new RedactedFolder(dpmHome!, "${DPM}", KeepRest: true));
        }
        folders.AddRange((workspaceRoots ?? []).Select(root => new RedactedFolder(root, "${WORKSPACE}", KeepRest: false)));
        return new DiagnosticRedactor(folders, [(Environment.UserName, "<user>"), (Environment.MachineName, "<machine>")]);
    }

    /// <summary>A masked copy of <paramref name="text"/>.</summary>
    public string Redact(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        text = RedactLines(text);
        text = Payload.Replace(text, m => $"\"{m.Groups[1].Value}\":\"<redacted {JsonStringLength(m.Groups[2].Value)} chars>\"");
        text = SensitiveText.MaskSecrets(text);
        text = Email.Replace(text, "<email>");
        text = AbsolutePath.Replace(text, m => ShortenPath(m.Value));
        text = Party.Replace(text, "<party>");
        text = QuotedParty.Replace(text, "'<party>'");
        text = ContractId.Replace(text, "<contract id>");
        foreach (var (pattern, placeholder) in _names)
        {
            text = pattern.Replace(text, placeholder);
        }
        return text;
    }

    // Compiler excerpts and script results span lines: a results section runs over the indented
    // (or blank) lines after its header and becomes one marker line.
    private static string RedactLines(string text)
    {
        var lines = text.Split('\n');
        var result = new StringBuilder(text.Length);
        var inResults = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var cr = line.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
            var content = line.Substring(0, line.Length - cr.Length);
            var prefix = LogPrefix.Match(content).Value;
            content = content.Substring(prefix.Length);
            string? output;

            if (inResults && (content.Length == 0 || char.IsWhiteSpace(content[0])))
            {
                output = null;
            }
            else
            {
                inResults = false;
                if (ResultHeader.Match(content) is { Success: true } header)
                {
                    inResults = true;
                    output = header.Groups[1].Value + (header.Groups[2].Length > 0 ? " " + Result : "");
                    output += "\n    " + ResultBlock;
                }
                else
                {
                    output = ExcerptLine.Replace(content, Code);
                    output = ContextLine.Replace(output, "$1 " + Code);
                    output = FailureStatus.Replace(output, "$1: " + Result);
                }
            }

            if (output is null)
            {
                continue;
            }
            if (result.Length > 0)
            {
                result.Append('\n');
            }
            result.Append(prefix).Append(output.Replace("\n", cr + "\n")).Append(cr);
        }
        // A trailing line break splits into a last empty line, which a results section swallows.
        if (text.EndsWith("\n", StringComparison.Ordinal) && !result.ToString().EndsWith("\n", StringComparison.Ordinal))
        {
            result.Append(text.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        }
        return result.ToString();
    }

    private string ShortenPath(string path)
    {
        var separator = path.IndexOf('\\') >= 0 ? "\\" : "/";
        var normalized = Normalize(path);
        foreach (var (folder, root) in _folders)
        {
            if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || (normalized.Length > root.Length && normalized[root.Length] != '\\'))
            {
                continue;
            }
            if (normalized.Length == root.Length)
            {
                return folder.Placeholder;
            }
            var rest = RestAfter(path, root.Length);
            return folder.KeepRest ? folder.Placeholder + rest : folder.Placeholder + FileOnly(rest, separator);
        }
        return "<path>" + FileOnly(path, separator);
    }

    // Only a name with an extension is taken for a file name worth keeping.
    private static string FileOnly(string path, string separator)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = trimmed.Substring(trimmed.LastIndexOfAny(['\\', '/']) + 1);
        return name.IndexOf('.') > 0 && name.Length < trimmed.Length ? $"{separator}...{separator}{name}" : "";
    }

    // The part of the original spelling after the first rootLength characters of its normalized form.
    private static string RestAfter(string path, int rootLength)
    {
        var consumed = 0;
        var index = 0;
        while (index < path.Length && consumed < rootLength)
        {
            index += path.Length - index >= 3 && path[index] == '%' ? 3 : 1;
            consumed++;
        }
        return path.Substring(index);
    }

    // Length of the text a JSON string literal stands for, as protocol transcripts count it.
    private static int JsonStringLength(string literal)
    {
        var length = 0;
        for (var i = 0; i < literal.Length; i++, length++)
        {
            if (literal[i] == '\\' && i + 1 < literal.Length)
            {
                i += literal[i + 1] == 'u' ? 5 : 1;
            }
        }
        return length;
    }

    // One spelling per path: backslashes, unescaped URI characters, no trailing separator.
    private static string Normalize(string path) =>
        Uri.UnescapeDataString(path.Replace('/', '\\')).TrimEnd('\\');
}
