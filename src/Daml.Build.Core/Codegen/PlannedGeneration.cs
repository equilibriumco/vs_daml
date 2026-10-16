namespace Daml.Build.Core.Codegen;

/// <summary>One generation as this build finds it.</summary>
/// <param name="Input">The reference and the DAR it reads.</param>
/// <param name="Folder">The folder its code lives in.</param>
/// <param name="Fingerprint">The fingerprint of this build's inputs for it.</param>
/// <param name="State">What its folder holds, judged against that fingerprint.</param>
public sealed record PlannedGeneration(CodegenInput Input, string Folder, string Fingerprint, GenerationState State);
