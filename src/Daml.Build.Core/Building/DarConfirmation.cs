using System;
using System.Collections.Generic;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Building;

/// <summary>Checks that a successful <c>dpm build</c> left its DAR where the manifest says it goes.</summary>
public static class DarConfirmation
{
    /// <summary>
    /// The DAR the build produced: the one dpm said it created, which has to be the one the
    /// manifest names, or, when dpm said nothing, the manifest's DAR if it is on disk.
    /// </summary>
    /// <param name="outcome">What dpm reported.</param>
    /// <param name="expectedDar">The DAR path read from the manifest.</param>
    /// <param name="manifestPath">The manifest, where a mismatch points.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="problems">Gets the reason when there is no DAR to hand on.</param>
    /// <returns>The DAR, or null with a problem added.</returns>
    public static string? Confirm(DpmOutcome outcome, string expectedDar, string manifestPath, Func<string, bool> fileExists, ICollection<BuildMessage> problems)
    {
        if (outcome is null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }
        if (fileExists is null)
        {
            throw new ArgumentNullException(nameof(fileExists));
        }
        if (problems is null)
        {
            throw new ArgumentNullException(nameof(problems));
        }
        if (outcome.CreatedDar is { } created)
        {
            if (DamlPath.Comparer.Equals(created, expectedDar))
            {
                return created;
            }
            problems.Add(Problem(DamlDiagnosticCodes.InvalidConfiguration, manifestPath, $"dpm wrote the DAR to {created}, but daml.yaml says it goes to {expectedDar}."));
            return null;
        }
        if (fileExists(expectedDar))
        {
            return expectedDar;
        }
        problems.Add(Problem(DamlDiagnosticCodes.CompileError, manifestPath, $"dpm build succeeded but left no DAR at {expectedDar}."));
        return null;
    }

    private static BuildMessage Problem(string code, string file, string text) =>
        new(DamlDiagnosticSeverity.Error, code, null, file, 0, 0, 0, 0, text);
}
