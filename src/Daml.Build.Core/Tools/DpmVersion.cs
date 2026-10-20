using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;

namespace Daml.Build.Core.Tools;

/// <summary>Which dpm a build runs, from <c>dpm --version</c>, and whether this build supports it.</summary>
public static class DpmVersion
{
    private const int QuotedLines = 5;

    // "version: 1.0.22"; a pre-release suffix ("-rc.2") doesn't make it another release.
    private static readonly Regex VersionLine = new(@"^version:\s*(?<version>\d+\.\d+\.\d+)", RegexOptions.CultureInvariant);

    /// <summary>The oldest dpm the build was tested with.</summary>
    public static Version Minimum => Codegen.ReleaseTools.MinimumDpm;

    /// <summary>The arguments that make dpm print its own version.</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["--version"];

    /// <summary>The version in <paramref name="output"/>, or null when there is none.</summary>
    public static Version? Parse(IEnumerable<string> output)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        foreach (var line in output)
        {
            var match = VersionLine.Match(DamlDiagnosticParser.StripAnsi(line).Trim());
            if (match.Success)
            {
                return Version.TryParse(match.Groups["version"].Value, out var version) ? version : null;
            }
        }
        return null;
    }

    /// <summary>Why the build can't use this dpm, or null when it can.</summary>
    /// <param name="version">The version dpm printed; null when it printed none.</param>
    /// <param name="output">What dpm printed, quoted when no version was in it.</param>
    public static ConfigurationError? Problem(Version? version, IReadOnlyList<string> output)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        if (version is null)
        {
            var printed = output.Select(l => SecretRedaction.Redact(DamlDiagnosticParser.StripAnsi(l).Trim())).Where(l => l.Length > 0).Take(QuotedLines);
            return new ConfigurationError(
                DamlDiagnosticCodes.DpmTooOld,
                $"'dpm --version' printed no version this Daml build knows, so it can't tell whether dpm is new enough ({Minimum} or later). It printed:\n" +
                string.Join("\n", printed));
        }
        return version < Minimum
            ? new ConfigurationError(
                DamlDiagnosticCodes.DpmTooOld,
                $"dpm {version} is older than {Minimum}, the oldest this Daml build supports. Install a newer dpm, " +
                $"or set {DamlProperties.SkipPreflight.Name} to true to build with this one at your own risk.")
            : null;
    }
}
