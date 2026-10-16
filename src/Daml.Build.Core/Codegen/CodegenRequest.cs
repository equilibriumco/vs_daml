using System.Collections.Generic;
using Daml.Build.Core.Configuration;

namespace Daml.Build.Core.Codegen;

/// <summary>Everything the C# generator works from for one project build, as plain data.</summary>
/// <param name="ProjectPath">The full path of the C# project.</param>
/// <param name="Configuration">The build's configuration.</param>
/// <param name="TargetFramework">The inner build's target framework.</param>
/// <param name="Tools">How tools may run.</param>
/// <param name="Codegen">The generator, the output mode and the generation root.</param>
/// <param name="Inputs">One input per generation, in declaration order.</param>
/// <param name="DesignTime">
/// A design-time build: nothing is generated, recovered or written; the result lists what the
/// last confirmed generations hold.
/// </param>
public sealed record CodegenRequest(
    string ProjectPath,
    string Configuration,
    string TargetFramework,
    ToolOptions Tools,
    CodegenOptions Codegen,
    IReadOnlyList<CodegenInput> Inputs,
    bool DesignTime);
