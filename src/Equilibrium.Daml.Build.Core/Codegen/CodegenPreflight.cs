using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.Build.Core.Tools;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>
/// Everything a real build checks before it generates: that the project can compile the code,
/// that dpm and Java are there and new enough, that the pinned generator is in its private
/// cache, and, for each input, whether its published generation is still current. Problems of
/// the project and its tools all come out of one run, and the generator is only touched when
/// there are none.
/// </summary>
public sealed class CodegenPreflight
{
    private const string Unchecked = "unchecked";

    private readonly IProcessRunner _runner;
    private readonly IDamlFileSystem _files;
    private readonly Func<string, string?> _environment;
    private readonly string _codegenRoot;
    private readonly string _locks;
    private readonly ToolVersionProbe _versions;

    /// <summary>Creates the preflight.</summary>
    /// <param name="runner">Runs dpm and java.</param>
    /// <param name="files">Where dpm, java and the DARs are looked for.</param>
    /// <param name="environment">Environment variables by name.</param>
    /// <param name="codegenRoot">The folder of the generators' private homes, usually <see cref="GeneratorCache.DefaultRoot"/>.</param>
    /// <param name="locks">The folder of the build's locks.</param>
    public CodegenPreflight(IProcessRunner runner, IDamlFileSystem files, Func<string, string?> environment, string codegenRoot, string locks)
        : this(runner, files, environment, codegenRoot, locks, ToolVersionCache.Shared)
    {
    }

    internal CodegenPreflight(IProcessRunner runner, IDamlFileSystem files, Func<string, string?> environment, string codegenRoot, string locks, ToolVersionCache versions)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _codegenRoot = codegenRoot ?? throw new ArgumentNullException(nameof(codegenRoot));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _versions = new ToolVersionProbe(_runner, versions);
    }

    /// <summary>
    /// Plans a real build's generations. A design-time request plans nothing and runs nothing:
    /// those builds only read what the last real one published.
    /// </summary>
    /// <param name="request">The project, its settings and inputs, already checked by the task.</param>
    /// <param name="cancellationToken">Stops waiting and running.</param>
    public async Task<CodegenPlan> PlanAsync(CodegenRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (request.DesignTime)
        {
            return new CodegenPlan([], [], null);
        }
        var pin = request.Codegen.Component ?? throw new ArgumentException("The request has no usable generator pin.", nameof(request));
        var root = request.Codegen.GeneratedRoot ?? throw new ArgumentException("The request has no usable generation root.", nameof(request));

        var problems = new List<ConfigurationError>(RuntimeCompatibility.Problems(request.TargetFramework, request.RuntimeVersion, request.LedgerAbstractionsVersion));
        var dpm = DpmLocation.Find(request.Tools.DpmPath, Path.GetDirectoryName(request.ProjectPath)!, _environment, _files, problems);
        var java = JavaLocation.Find(request.Codegen.JavaHome, _environment, _files, problems);
        var dpmVersion = Unchecked;
        if (!request.Tools.SkipPreflight)
        {
            if (dpm is not null)
            {
                var (version, problem) = await _versions.DpmAsync(dpm, null, cancellationToken).ConfigureAwait(false);
                if (problem is not null)
                {
                    problems.Add(problem);
                }
                dpmVersion = version?.ToString() ?? Unchecked;
            }
            if (java is not null)
            {
                var (output, _) = await _versions.AskAsync(java, JavaVersion.Arguments, stderr: true, null, cancellationToken).ConfigureAwait(false);
                if (JavaVersion.Problem(JavaVersion.Parse(output), java, output) is { } problem)
                {
                    problems.Add(problem);
                }
            }
        }
        if (problems.Count > 0 || dpm is null || java is null)
        {
            return new CodegenPlan(problems, [], null);
        }

        var cache = new GeneratorCache(_codegenRoot, pin);
        if (await new GeneratorPreparation(_runner, _locks).PrepareAsync(cache, dpm, request.Tools, cancellationToken).ConfigureAwait(false) is { } unprepared)
        {
            return new CodegenPlan([unprepared], [], null);
        }

        var generations = new List<PlannedGeneration>();
        foreach (var input in request.Inputs)
        {
            if (!_files.FileExists(input.DarPath))
            {
                problems.Add(new ConfigurationError(
                    DamlDiagnosticCodes.CodegenFailed,
                    $"There is no DAR at {input.DarPath} to generate from. Build {input.Reference.Include} first."));
                continue;
            }
            var folder = Path.Combine(root, input.Reference.FolderName);
            var darSha256 = Hashing.Sha256File(input.DarPath);
            var fingerprint = GenerationFingerprint.Of(input, darSha256, pin.Digest, dpmVersion);
            var state = GenerationState.Evaluate(folder, fingerprint, request.ProjectPath);
            if (state.Status == GenerationStatus.Conflict)
            {
                problems.Add(new ConfigurationError(DamlDiagnosticCodes.OutputConflict, state.Reason!));
            }
            generations.Add(new PlannedGeneration(input, folder, fingerprint, state) { DarSha256 = darSha256 });
        }
        return new CodegenPlan(problems, generations, new CodegenTools(dpm, java, dpmVersion, cache));
    }
}
