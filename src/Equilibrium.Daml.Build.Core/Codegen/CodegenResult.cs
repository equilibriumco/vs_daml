using System.Collections.Generic;
using Equilibrium.Daml.Build.Core.Building;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>What a codegen run left for the compiler.</summary>
/// <param name="Files">Exactly the files of the current generations, in a stable order.</param>
/// <param name="Messages">What went wrong or is worth knowing, errors included.</param>
public sealed record CodegenResult(IReadOnlyList<GeneratedFile> Files, IReadOnlyList<BuildMessage> Messages);
