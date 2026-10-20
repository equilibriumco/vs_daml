using System;
using System.Text;

namespace Daml.Build.Core.Manifests;

/// <summary>DPM's raw package-YAML expansion, following Go os.Expand.</summary>
public static class PackageEnvironment
{
    /// <summary>Expands shell-style variable references; an unset variable is a configuration error.</summary>
    public static string Expand(string text, Func<string, string?> environment)
    {
        if (text is null) { throw new ArgumentNullException(nameof(text)); }
        if (environment is null) { throw new ArgumentNullException(nameof(environment)); }
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '$' || i + 1 == text.Length)
            {
                result.Append(text[i]);
                continue;
            }
            var start = i + 1;
            string name;
            if (text[start] == '{')
            {
                var end = text.IndexOf('}', start + 1);
                if (end < 0)
                {
                    i = start;
                    continue;
                }
                name = text.Substring(start + 1, end - start - 1);
                i = end;
            }
            else if ("*#$@!?-0123456789".IndexOf(text[start]) >= 0)
            {
                name = text[start].ToString();
                i = start;
            }
            else
            {
                var end = start;
                while (end < text.Length && (text[end] is '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))) { end++; }
                if (end == start)
                {
                    result.Append('$');
                    continue;
                }
                name = text.Substring(start, end - start);
                i = end - 1;
            }
            if (name.Length > 0)
            {
                result.Append(environment(name) ?? throw new FormatException($"Environment variable '{name}' used in daml.yaml is not set."));
            }
        }
        return result.ToString();
    }
}
