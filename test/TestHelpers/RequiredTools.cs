using System;
using Xunit;

namespace Equilibrium.Daml.TestHelpers;

/// <summary>
/// Gate for tests that need an external tool. Locally a missing tool skips the test; in gate
/// runs (<c>EQDAML_REQUIRED_TOOLS=1</c>, set by <c>scripts/test.ps1 -RequiredTools</c>) it fails it,
/// so a machine without the toolchain can't produce a green result by skipping everything.
/// </summary>
/// <remarks>Tests that call this must be <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>.</remarks>
public static class RequiredTools
{
    /// <summary>Environment variable that switches missing tools from "skip" to "fail".</summary>
    public const string EnvironmentVariable = "EQDAML_REQUIRED_TOOLS";

    /// <summary>Whether this run is a gate run.</summary>
    public static bool IsRequiredMode => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    /// <summary>Returns the tool's full path, or skips/fails the current test when it is missing.</summary>
    public static string Require(ExternalTool tool) => Require(tool, ToolLocator.Find(tool), IsRequiredMode);

    internal static string Require(ExternalTool tool, string? path, bool requiredMode)
    {
        if (path is not null)
        {
            return path;
        }

        var name = tool.ToString().ToLowerInvariant();
        if (requiredMode)
        {
            throw new RequiredToolMissingException(
                $"{name} is required in this run ({EnvironmentVariable}=1) but was not found. Run scripts/bootstrap.ps1 to see what is missing.");
        }

        throw new SkipException($"{name} is not installed; run scripts/bootstrap.ps1 -Install to enable this test.");
    }
}
