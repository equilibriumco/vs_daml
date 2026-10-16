namespace Daml.Build.Core.Codegen;

/// <summary>A file of a generation, as its manifest records it.</summary>
/// <param name="Path">The path relative to the generation's folder, with backslashes.</param>
/// <param name="Sha256">Its SHA-256 when it was published.</param>
public sealed record GenerationManifestFile(string Path, string Sha256);
