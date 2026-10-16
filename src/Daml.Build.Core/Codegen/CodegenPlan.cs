using System.Collections.Generic;
using Daml.Build.Core.Configuration;

namespace Daml.Build.Core.Codegen;

/// <summary>What the preflight found: the problems that stop the build, or the generations and the tools to bring them up to date.</summary>
/// <param name="Problems">Everything that stops the build; when not empty, nothing may be generated.</param>
/// <param name="Generations">One per input that could be judged, in input order.</param>
/// <param name="Tools">The checked tools; null when the preflight stopped early or the build is a design-time one.</param>
public sealed record CodegenPlan(IReadOnlyList<ConfigurationError> Problems, IReadOnlyList<PlannedGeneration> Generations, CodegenTools? Tools);
