using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;

namespace Daml.Build.Core.Tools;

/// <summary>Which Java the generator would run on, from <c>java -version</c>, and whether it is new enough.</summary>
public static class JavaVersion
{
    private const int QuotedLines = 5;

    // 'openjdk version "21.0.12" 2025-10-21' or 'java version "26.0.2.1"'; before Java 9 it was "1.8.0_391".
    private static readonly Regex VersionLine = new(@"version ""(?<version>[^""]+)""", RegexOptions.CultureInvariant);

    /// <summary>The arguments that make java print its version (on standard error).</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["-version"];

    /// <summary>The major version in <paramref name="output"/>, or null when there is none.</summary>
    public static int? Parse(IEnumerable<string> output)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        foreach (var line in output)
        {
            var match = VersionLine.Match(line);
            if (!match.Success)
            {
                continue;
            }
            var parts = match.Groups["version"].Value.Split('.', '_', '-', '+');
            var major = parts[0] == "1" && parts.Length > 1 ? parts[1] : parts[0];
            return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
        }
        return null;
    }

    /// <summary>Why the generator can't run on this Java, or null when it can.</summary>
    /// <param name="major">The major version java printed; null when it printed none.</param>
    /// <param name="java">The java that was asked.</param>
    /// <param name="output">What it printed, quoted when no version was in it.</param>
    public static ConfigurationError? Problem(int? major, string java, IReadOnlyList<string> output)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        if (major is null)
        {
            var printed = output.Select(l => l.Trim()).Where(l => l.Length > 0).Take(QuotedLines);
            return new ConfigurationError(
                DamlDiagnosticCodes.MissingJava,
                $"'{java} -version' printed no version, so the build can't tell whether it is JDK {ReleaseTools.MinimumJava} or later. It printed:\n" +
                string.Join("\n", printed));
        }
        return major < ReleaseTools.MinimumJava
            ? new ConfigurationError(
                DamlDiagnosticCodes.MissingJava,
                $"Java {major} ({java}) is older than {ReleaseTools.MinimumJava}, which the C# code generator needs. " +
                $"Install JDK {ReleaseTools.MinimumJava} or later and point {DamlProperties.JavaHome.Name} or JAVA_HOME at it.")
            : null;
    }
}
