namespace Daml.Build.Core.Codegen;

/// <summary>The tools a planned generation runs with, found and checked by the preflight.</summary>
/// <param name="Dpm">The dpm that runs the generator.</param>
/// <param name="Java">The java the generator runs on; its folder goes first on the generator's PATH.</param>
/// <param name="DpmVersion">dpm's version, or <c>unchecked</c> when the preflight was skipped.</param>
/// <param name="Cache">The generator's private cache, ready.</param>
public sealed record CodegenTools(string Dpm, string Java, string DpmVersion, GeneratorCache Cache);
