namespace Daml.Build.Core.Codegen;

/// <summary>What a generation's code depends on, besides the release itself.</summary>
/// <param name="GenerationId">The identity: reference, namespace and generator digest.</param>
/// <param name="DarSha256">The SHA-256 of the DAR it reads.</param>
/// <param name="GeneratorDigest">The pinned generator's digest.</param>
/// <param name="DpmVersion">The dpm that runs the generator.</param>
public sealed record GenerationFingerprintInputs(string GenerationId, string DarSha256, string GeneratorDigest, string DpmVersion);
