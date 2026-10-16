using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;

namespace Daml.Build.Core.Codegen;

/// <summary>Whether a C# project can compile the code this release's generator writes.</summary>
public static class RuntimeCompatibility
{
    private static readonly Regex ModernFramework = new(@"^net(?<major>\d+)\.(?<minor>\d+)(-.+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex VersionStart = new(@"^(?<major>\d+)\.(?<minor>\d+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// What stands in the way: a target framework the runtime doesn't support, or a runtime
    /// missing or from another line than the generator writes for. Checked before anything is
    /// generated, so the project fails with a reason instead of a pile of compiler errors.
    /// </summary>
    /// <param name="targetFramework">The inner build's <c>$(TargetFramework)</c>, e.g. <c>net10.0</c>.</param>
    /// <param name="runtimeVersion">The resolved version of the runtime package; null when the project doesn't reference it.</param>
    public static IReadOnlyList<ConfigurationError> Problems(string targetFramework, string? runtimeVersion)
    {
        var (package, line) = ReleaseTools.Runtime;
        var problems = new List<ConfigurationError>();
        if (!Supported(targetFramework))
        {
            problems.Add(Problem(
                $"The project targets {targetFramework}, but the generated bindings use {package} {line}, which supports " +
                $"net{ReleaseTools.MinimumTargetFramework} and later only. Target net{ReleaseTools.MinimumTargetFramework} or later."));
        }
        if (string.IsNullOrWhiteSpace(runtimeVersion))
        {
            problems.Add(Problem(
                $"The generated bindings need {package} {line}, which the project doesn't reference. " +
                $"Add <PackageReference Include=\"{package}\" Version=\"{line}.0\" />."));
        }
        else if (Line(runtimeVersion!) != line)
        {
            problems.Add(Problem(
                $"The project references {package} {runtimeVersion}, but the generator this release ships with writes code for {package} {line}. " +
                $"Use a {line} version of {package}."));
        }
        return problems;
    }

    private static bool Supported(string targetFramework)
    {
        var match = ModernFramework.Match(targetFramework ?? "");
        return match.Success && Numbers(match) >= ReleaseTools.MinimumTargetFramework;
    }

    private static Version? Line(string version)
    {
        var match = VersionStart.Match(version.Trim());
        return match.Success ? Numbers(match) : null;
    }

    private static Version Numbers(Match match) => new(
        int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
        int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture));

    private static ConfigurationError Problem(string message) => new(DamlDiagnosticCodes.RuntimeMismatch, message);
}
