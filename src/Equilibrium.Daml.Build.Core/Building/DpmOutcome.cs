using System.Collections.Generic;

namespace Equilibrium.Daml.Build.Core.Building;

/// <summary>What a dpm run reported, read from its output once it ended.</summary>
/// <param name="Messages">Diagnostics and failures, in output order, the explanation of a failure last.</param>
/// <param name="CreatedDar">The full path of the DAR dpm said it created; null when it said none.</param>
public sealed record DpmOutcome(IReadOnlyList<BuildMessage> Messages, string? CreatedDar);
