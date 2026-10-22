using System.Collections.Generic;
using System.IO;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.FileSystem;
using Microsoft.Build.Framework;

namespace Equilibrium.Daml.Build.Tasks;

/// <summary>
/// Clean for a C# project's generated bindings: removes what the generator wrote under the
/// project's generation root, under the lock every build of that root takes, and nothing it
/// didn't write. The generators' caches are never touched.
/// </summary>
public sealed class DamlCleanGenerated : OrchestratorTask
{
    /// <summary>Creates the task with the orchestrator of this machine.</summary>
    public DamlCleanGenerated()
        : base(null)
    {
    }

    internal DamlCleanGenerated(ICodegenOrchestrator orchestrator)
        : base(orchestrator)
    {
    }

    /// <summary>The C# project, <c>$(MSBuildProjectFullPath)</c>.</summary>
    [Required]
    public string ProjectPath { get; set; } = "";

    /// <summary><c>$(IntermediateOutputPath)</c> of this inner build.</summary>
    [Required]
    public string IntermediateOutputPath { get; set; } = "";

    /// <summary><c>$(Configuration)</c>.</summary>
    public string? Configuration { get; set; }

    /// <summary><c>$(TargetFramework)</c> of this inner build.</summary>
    public string? TargetFramework { get; set; }

    /// <summary><c>DamlGeneratedOutputMode</c>.</summary>
    public string? GeneratedOutputMode { get; set; }

    /// <summary><c>DamlGeneratedDir</c>.</summary>
    public string? GeneratedDir { get; set; }

    /// <summary><c>DamlLockTimeoutSeconds</c>.</summary>
    public string? LockTimeoutSeconds { get; set; }

    /// <inheritdoc />
    public override bool Execute()
    {
        var projectPath = DamlPath.Normalize(ProjectPath);
        var properties = new Dictionary<string, string?>
        {
            ["MSBuildProjectDirectory"] = Path.GetDirectoryName(projectPath),
            ["IntermediateOutputPath"] = IntermediateOutputPath,
            [DamlProperties.GeneratedOutputMode.Name] = GeneratedOutputMode,
            [DamlProperties.GeneratedDir.Name] = GeneratedDir,
            [DamlProperties.LockTimeoutSeconds.Name] = LockTimeoutSeconds,
        };
        string? Property(string name) => properties.TryGetValue(name, out var value) ? value : null;

        var errors = new List<ConfigurationError>();
        var tools = ToolOptions.Read(Property, errors);
        var codegen = CodegenOptions.Read(Property, errors);
        foreach (var error in errors)
        {
            Log.LogError(null, error.Code, null, projectPath, 0, 0, 0, 0, error.Message);
        }
        if (errors.Count > 0)
        {
            return false;
        }

        var request = new CodegenRequest(projectPath, Configuration ?? "", TargetFramework ?? "", tools, codegen, [], DesignTime: false, RuntimeVersion: null);
        return Run(
                projectPath,
                "Cleaning the generated C# was cancelled; what wasn't removed yet is still there.",
                (orchestrator, cancellationToken) => orchestrator.CleanAsync(request, cancellationToken)) is not null
            && !Log.HasLoggedErrors;
    }
}
