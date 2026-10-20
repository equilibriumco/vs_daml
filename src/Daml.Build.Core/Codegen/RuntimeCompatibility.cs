using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;

namespace Daml.Build.Core.Codegen;

/// <summary>Whether a C# project can compile the code this release's generator writes.</summary>
public static class RuntimeCompatibility
{
    private static readonly Regex ModernFramework = new(@"^net(?<major>\d+)\.(?<minor>\d+)(-.+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// What stands in the way: a target framework the runtime doesn't support, or a runtime
    /// missing or different from the tested version. Checked before anything is
    /// generated, so the project fails with a reason instead of a pile of compiler errors.
    /// </summary>
    /// <param name="targetFramework">The inner build's <c>$(TargetFramework)</c>, e.g. <c>net10.0</c>.</param>
    /// <param name="runtimeVersion">The resolved version of the runtime package; null when the project doesn't reference it.</param>
    public static IReadOnlyList<ConfigurationError> Problems(string targetFramework, string? runtimeVersion)
    {
        var package = ReleaseTools.Runtime.Package;
        var version = ReleaseTools.RuntimeVersion;
        var problems = new List<ConfigurationError>();
        if (!Supported(targetFramework))
        {
            problems.Add(Problem(
                $"The project targets {targetFramework}, but the generated bindings use {package} {version}, which supports " +
                $"net{ReleaseTools.MinimumTargetFramework} and later only. Target net{ReleaseTools.MinimumTargetFramework} or later."));
        }
        if (string.IsNullOrWhiteSpace(runtimeVersion))
        {
            problems.Add(Problem(
                $"The generated bindings need {package} {version}, which the project doesn't reference. " +
                $"Add <PackageReference Include=\"{package}\" Version=\"{version}\" />."));
        }
        else if (!string.Equals(runtimeVersion!.Trim(), version, StringComparison.Ordinal))
        {
            problems.Add(Problem(
                $"The project references {package} {runtimeVersion}, but this generator is tested with {package} {version}. " +
                $"Use Daml.Runtime and Daml.Ledger.Abstractions {version} together."));
        }
        return problems;
    }

    /// <summary>Checks both packages against the exact versions tested with the emitter.</summary>
    public static IReadOnlyList<ConfigurationError> Problems(string targetFramework, string? runtimeVersion, string? ledgerAbstractionsVersion)
    {
        var problems = new List<ConfigurationError>(Problems(targetFramework, runtimeVersion));
        if (!string.Equals(ledgerAbstractionsVersion?.Trim(), ReleaseTools.RuntimeVersion, StringComparison.Ordinal))
        {
            problems.Add(Problem($"The generated bindings need Daml.Ledger.Abstractions {ReleaseTools.RuntimeVersion}; " +
                $"the resolved version is '{ledgerAbstractionsVersion ?? "missing"}'. Use Daml.Runtime and Daml.Ledger.Abstractions {ReleaseTools.RuntimeVersion} together."));
        }
        return problems;
    }

    private static bool Supported(string targetFramework)
    {
        var match = ModernFramework.Match(targetFramework ?? "");
        return match.Success && Version.TryParse(match.Groups["major"].Value + "." + match.Groups["minor"].Value, out var version)
            && version >= ReleaseTools.MinimumTargetFramework;
    }

    private static ConfigurationError Problem(string message) => new(DamlDiagnosticCodes.RuntimeMismatch, message);
}
