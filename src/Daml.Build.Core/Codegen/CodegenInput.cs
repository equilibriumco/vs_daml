namespace Daml.Build.Core.Codegen;

/// <summary>One generation to keep up to date: a checked reference and the DAR it reads.</summary>
/// <param name="Reference">The reference with its identity.</param>
/// <param name="DarPath">The DAR to generate from: the one a Daml project built, or the referenced DAR itself.</param>
/// <param name="Source">For a project reference, the sources its DAR was built from; null when unknown or for a DAR reference.</param>
public sealed record CodegenInput(GenerationReference Reference, string DarPath, SourceProvenance? Source = null);
