namespace Daml.Build.Core.Codegen;

/// <summary>One generation to keep up to date: a checked reference and the DAR it reads.</summary>
/// <param name="Reference">The reference with its identity.</param>
/// <param name="DarPath">The DAR to generate from: the one a Daml project built, or the referenced DAR itself.</param>
public sealed record CodegenInput(GenerationReference Reference, string DarPath);
