namespace Daml.Build.Core.Codegen;

/// <summary>A file the generator owns, to be compiled.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="GenerationId">The generation it belongs to.</param>
public sealed record GeneratedFile(string Path, string GenerationId);
