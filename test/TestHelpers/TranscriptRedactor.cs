using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Equilibrium.Daml.VisualStudio.Core.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Equilibrium.Daml.TestHelpers;

/// <summary>
/// Makes a recorded LSP message safe to keep outside the machine: known folders become
/// placeholders in every spelling a server uses (backslashes, slashes, <c>file:</c> URIs with an
/// escaped drive colon, any case), document text and virtual resource contents are replaced by
/// their length, and anything that looks like a credential is masked.
/// </summary>
public sealed class TranscriptRedactor
{
    private readonly List<(Regex Pattern, string Placeholder)> _paths;

    /// <summary>Creates a redactor for the given folders; the longest folder wins where they nest.</summary>
    public TranscriptRedactor(IEnumerable<(string Path, string Placeholder)> paths)
    {
        _paths = (paths ?? throw new ArgumentNullException(nameof(paths)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Path))
            .OrderByDescending(p => p.Path.Length)
            .Select(p => (PathPattern(p.Path), p.Placeholder))
            .ToList();
    }

    /// <summary>A redacted copy of <paramref name="message"/>.</summary>
    public JObject Redact(JObject message)
    {
        var copy = (JObject)(message ?? throw new ArgumentNullException(nameof(message))).DeepClone();
        RedactCode(copy);
        // A different number on every run would make every recording differ from the last.
        if ((string?)copy["method"] == "initialize" && copy.SelectToken("params.processId") is JValue { Type: JTokenType.Integer } processId)
        {
            processId.Value = 0;
        }
        return (JObject)RedactToken(copy);
    }

    /// <summary>Redacts paths and secrets in one piece of text.</summary>
    public string RedactText(string text)
    {
        foreach (var (pattern, placeholder) in _paths)
        {
            text = pattern.Replace(text, placeholder.Replace("$", "$$"));
        }
        return SensitiveText.MaskSecrets(text);
    }

    private JToken RedactToken(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                var result = new JObject();
                foreach (var property in obj.Properties())
                {
                    result[RedactText(property.Name)] = RedactToken(property.Value);
                }
                return result;
            case JArray array:
                return new JArray(array.Select(RedactToken));
            case JValue { Type: JTokenType.String } value:
                return new JValue(RedactText((string)value!));
            default:
                return token.DeepClone();
        }
    }

    // Source code and script results stay on the machine; their size is enough for a replay.
    private static void RedactCode(JObject message)
    {
        switch ((string?)message["method"])
        {
            case "textDocument/didOpen":
                Replace(message.SelectToken("params.textDocument.text"));
                break;
            case "textDocument/didChange":
                foreach (var change in message.SelectTokens("params.contentChanges[*].text").ToList())
                {
                    Replace(change);
                }
                break;
            case "daml/virtualResource/didChange":
                Replace(message.SelectToken("params.contents"));
                break;
        }

        static void Replace(JToken? text)
        {
            if (text is JValue { Type: JTokenType.String } value)
            {
                value.Replace(new JValue($"<redacted {((string)value!).Length} chars>"));
            }
        }
    }

    // C:\Users\me\x matches as C:/Users/me/x, c%3A/users/ME/x and with %20 for spaces, but not
    // C:\Users\me\xy.
    private static Regex PathPattern(string path)
    {
        var parts = path.TrimEnd('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var pattern = string.Join(@"[\\/]", parts.Select(Part));
        return new Regex(pattern + @"(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        static string Part(string part)
        {
            if (part.Length == 2 && part[1] == ':')
            {
                return Regex.Escape(part[0].ToString()) + "(?::|%3A)";
            }
            var escaped = Uri.EscapeDataString(part);
            return escaped == part ? Regex.Escape(part) : $"(?:{Regex.Escape(part)}|{Regex.Escape(escaped)})";
        }
    }
}
