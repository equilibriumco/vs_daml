using System.Collections.Generic;
using Equilibrium.Daml.Build.Core.Configuration;

namespace Equilibrium.Daml.Build.Core.Codegen;

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
/// <param name="RuntimeVersion">The version of the runtime package the project resolved; null when it doesn't reference it.</param>
public sealed record CodegenRequest(
    string ProjectPath,
    string Configuration,
    string TargetFramework,
    ToolOptions Tools,
    CodegenOptions Codegen,
    IReadOnlyList<CodegenInput> Inputs,
    bool DesignTime,
    string? RuntimeVersion)
{
    /// <summary>The resolved Daml.Ledger.Abstractions version; null when absent.</summary>
    public string? LedgerAbstractionsVersion { get; init; }
}
