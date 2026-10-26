namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>One generation as this build finds it.</summary>
/// <param name="Input">The reference and the DAR it reads.</param>
/// <param name="Folder">The folder its code lives in.</param>
/// <param name="Fingerprint">The fingerprint of this build's inputs for it.</param>
/// <param name="State">What its folder holds, judged against that fingerprint.</param>
public sealed record PlannedGeneration(CodegenInput Input, string Folder, string Fingerprint, GenerationState State)
{
    /// <summary>
    /// The SHA-256 of the DAR the fingerprint was computed from; null when unknown. A DAR that
    /// hashes differently later was rebuilt in the meantime and needs a fingerprint of its own.
    /// </summary>
    public string? DarSha256 { get; init; }
}
