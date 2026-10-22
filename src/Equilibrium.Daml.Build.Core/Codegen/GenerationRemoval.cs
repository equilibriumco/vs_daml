using System.Collections.Generic;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>What removing a generation did.</summary>
/// <param name="Owned">False when the folder holds another project's generation, which was left as it was.</param>
/// <param name="KeptFiles">Files in the folder the generator didn't write, relative to it; they and their folders stay.</param>
public sealed record GenerationRemoval(bool Owned, IReadOnlyList<string> KeptFiles);
