using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

/// <summary>
/// Keeps a C# project's bindings in step with the DARs it references and hands the generated
/// files to the compiler. Settings and references are checked first, all at once; the work
/// itself is the codegen orchestrator's.
/// </summary>
public sealed class DamlCodegenCSharp : OrchestratorTask
{
    /// <summary>Creates the task with the orchestrator of this machine.</summary>
    public DamlCodegenCSharp()
        : base(null)
    {
    }

    internal DamlCodegenCSharp(ICodegenOrchestrator orchestrator)
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
    [Required]
    public string Configuration { get; set; } = "";

    /// <summary><c>$(TargetFramework)</c> of this inner build.</summary>
    [Required]
    public string TargetFramework { get; set; } = "";

    /// <summary><c>$(TargetFrameworks)</c>; empty for a project with a single target framework.</summary>
    public string? TargetFrameworks { get; set; }

    /// <summary>
    /// The Daml references as written, each with <c>DamlReferenceSource</c> (<c>DamlReference</c>,
    /// <c>DamlDar</c> or <c>ProjectReference</c>), its <c>Namespace</c> and, for a project, the
    /// <c>DarPath</c> the project returned.
    /// </summary>
    public ITaskItem[] References { get; set; } = [];

    /// <summary>A design-time build, which reads the last generations and writes nothing.</summary>
    public bool DesignTime { get; set; }

    /// <summary><c>DamlCodegenEnabled</c>.</summary>
    public string? CodegenEnabled { get; set; }

    /// <summary><c>DamlCodegenComponent</c>.</summary>
    public string? CodegenComponent { get; set; }

    /// <summary><c>DamlGeneratedOutputMode</c>.</summary>
    public string? GeneratedOutputMode { get; set; }

    /// <summary><c>DamlGeneratedDir</c>.</summary>
    public string? GeneratedDir { get; set; }

    /// <summary><c>DamlJavaHome</c>.</summary>
    public string? JavaHome { get; set; }

    /// <summary><c>DamlDpmPath</c>.</summary>
    public string? DpmPath { get; set; }

    /// <summary><c>DamlOffline</c>.</summary>
    public string? Offline { get; set; }

    /// <summary><c>DamlAllowToolInstall</c>.</summary>
    public string? AllowToolInstall { get; set; }

    /// <summary><c>DamlSkipPreflight</c>.</summary>
    public string? SkipPreflight { get; set; }

    /// <summary><c>DamlProcessTimeoutSeconds</c>.</summary>
    public string? ProcessTimeoutSeconds { get; set; }

    /// <summary><c>DamlLockTimeoutSeconds</c>.</summary>
    public string? LockTimeoutSeconds { get; set; }

    /// <summary>The version of <c>Daml.Runtime</c> the project resolved; empty when it doesn't reference it.</summary>
    public string? RuntimeVersion { get; set; }

    /// <summary>The generated files to compile, with <c>DamlGenerated=true</c> and their <c>DamlGenerationId</c>.</summary>
    [Output]
    public ITaskItem[] GeneratedFiles { get; private set; } = [];

    /// <inheritdoc />
    public override bool Execute()
    {
        var projectPath = DamlPath.Normalize(ProjectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var properties = new Dictionary<string, string?>
        {
            ["MSBuildProjectDirectory"] = projectDirectory,
            ["IntermediateOutputPath"] = IntermediateOutputPath,
            ["TargetFrameworks"] = TargetFrameworks,
            [DamlProperties.CodegenEnabled.Name] = CodegenEnabled,
            [DamlProperties.CodegenComponent.Name] = CodegenComponent,
            [DamlProperties.GeneratedOutputMode.Name] = GeneratedOutputMode,
            [DamlProperties.GeneratedDir.Name] = GeneratedDir,
            [DamlProperties.JavaHome.Name] = JavaHome,
            [DamlProperties.DpmPath.Name] = DpmPath,
            [DamlProperties.Offline.Name] = Offline,
            [DamlProperties.AllowToolInstall.Name] = AllowToolInstall,
            [DamlProperties.SkipPreflight.Name] = SkipPreflight,
            [DamlProperties.ProcessTimeoutSeconds.Name] = ProcessTimeoutSeconds,
            [DamlProperties.LockTimeoutSeconds.Name] = LockTimeoutSeconds,
        };
        string? Property(string name) => properties.TryGetValue(name, out var value) ? value : null;

        var errors = new List<ConfigurationError>();
        var tools = ToolOptions.Read(Property, errors);
        var codegen = CodegenOptions.Read(Property, errors);
        if (!codegen.Enabled && errors.Count == 0)
        {
            return true;
        }
        // With an unusable pin the references are still checked, so one build reports everything.
        var digest = codegen.Component?.Digest ?? CodegenComponentPin.TryParse(DamlProperties.CodegenComponent.Default)!.Digest;
        var declared = References.Select(Declared).ToList();
        var inputs = Inputs(GenerationReferences.Resolve(declared, projectDirectory, digest, errors), errors);
        foreach (var error in errors)
        {
            Log.LogError(null, error.Code, null, projectPath, 0, 0, 0, 0, error.Message);
        }
        if (errors.Count > 0)
        {
            return false;
        }

        var request = new CodegenRequest(projectPath, Configuration, TargetFramework, tools, codegen, inputs, DesignTime, RuntimeVersion);
        var result = Run(
            projectPath,
            "Generating C# from DARs was cancelled; whatever was generated before is as it was.",
            (orchestrator, cancellationToken) => orchestrator.RunAsync(request, cancellationToken));
        if (result is null)
        {
            return false;
        }
        GeneratedFiles = result.Files.Select(Compile).ToArray();
        return !Log.HasLoggedErrors;
    }

    private static DeclaredDamlReference Declared(ITaskItem item) => new(
        Enum.TryParse<DamlReferenceSource>(item.GetMetadata("DamlReferenceSource"), out var source) ? source : DamlReferenceSource.DamlReference,
        item.ItemSpec,
        item.GetMetadata("Namespace"));

    // A project's DAR, and the sources it was built from, come from the project; a DAR reference is its own input.
    private List<CodegenInput> Inputs(IReadOnlyList<GenerationReference> references, List<ConfigurationError> errors)
    {
        var projectDirectory = Path.GetDirectoryName(DamlPath.Normalize(ProjectPath))!;
        var darByProject = new Dictionary<string, string>(DamlPath.Comparer);
        var sourcesByProject = new Dictionary<string, SourceProvenance>(DamlPath.Comparer);
        foreach (var item in References)
        {
            var dar = item.GetMetadata("DarPath");
            var project = DamlPath.Resolve(projectDirectory, item.ItemSpec);
            if (dar.Length > 0 && !darByProject.ContainsKey(project))
            {
                darByProject.Add(project, dar);
            }
            var (manifest, fingerprint) = (item.GetMetadata("DamlManifest"), item.GetMetadata("DamlSourceFingerprint"));
            if (manifest.Length > 0 && fingerprint.Length > 0 && !sourcesByProject.ContainsKey(project))
            {
                sourcesByProject.Add(project, new SourceProvenance(DamlPath.Resolve(projectDirectory, manifest), fingerprint));
            }
        }
        var inputs = new List<CodegenInput>();
        foreach (var reference in references)
        {
            var dar = reference.Kind == DamlReferenceKind.Dar ? reference.Path : darByProject.TryGetValue(reference.Path, out var path) ? path : "";
            if (string.IsNullOrEmpty(dar))
            {
                errors.Add(new ConfigurationError(
                    DamlDiagnosticCodes.CodegenFailed,
                    $"{reference.Include} didn't say which DAR it builds, so there is nothing to generate from. Check that it is a Daml project."));
                continue;
            }
            var sources = reference.Kind == DamlReferenceKind.Project && sourcesByProject.TryGetValue(reference.Path, out var found) ? found : null;
            inputs.Add(new CodegenInput(reference, DamlPath.Resolve(projectDirectory, dar), sources));
        }
        return inputs;
    }

    private static ITaskItem Compile(GeneratedFile file)
    {
        var item = new TaskItem(file.Path);
        item.SetMetadata("DamlGenerated", "true");
        item.SetMetadata("DamlGenerationId", file.GenerationId);
        return item;
    }
}
