namespace Equilibrium.Daml.TestHelpers;

/// <summary>
/// Exit codes the fake dpm uses for its own failures. Compiled into both the fake executable
/// and the helpers that drive it, so the two can't drift apart.
/// </summary>
internal static class FakeDpmExitCodes
{
    /// <summary>The scenario has no entry for the command.</summary>
    public const int Unscripted = 97;

    /// <summary>The scenario file is missing or invalid.</summary>
    public const int ScenarioError = 98;
}
