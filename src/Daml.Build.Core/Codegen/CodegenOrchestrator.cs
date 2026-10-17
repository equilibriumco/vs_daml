using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Building;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Locking;
using Daml.Build.Core.Processes;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// Keeps a project's generated C# in step with the DARs it references. A real build checks the
/// project and its tools first, brings every generation up to date, and hands the compiler
/// exactly the files of the current generations. A design-time build only reads what the last
/// real build published: it runs nothing, takes no lock, and leaves anything half done for the
/// next real build.
/// </summary>
public sealed class CodegenOrchestrator : ICodegenOrchestrator
{
    private const int ListedFiles = 3;

    private readonly CodegenPreflight _preflight;
    private readonly GenerationUpdate _update;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="preflight">Checks the project and its tools and plans the generations.</param>
    /// <param name="update">Brings the planned generations up to date.</param>
    public CodegenOrchestrator(CodegenPreflight preflight, GenerationUpdate update)
    {
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _update = update ?? throw new ArgumentNullException(nameof(update));
    }

    /// <summary>
    /// The orchestrator a build on this machine uses: real processes in kill-on-close jobs, the
    /// generators' private homes and the locks under <c>%LOCALAPPDATA%\DamlVisualStudio</c>.
    /// </summary>
    /// <param name="environment">Environment variables by name.</param>
    /// <exception cref="InvalidOperationException"><c>LOCALAPPDATA</c> isn't set.</exception>
    public static CodegenOrchestrator ForThisMachine(Func<string, string?> environment)
    {
        if (environment is null)
        {
            throw new ArgumentNullException(nameof(environment));
        }
        var runner = new ProcessRunner();
        var locks = LockNames.Root(environment);
        return new CodegenOrchestrator(
            new CodegenPreflight(runner, new PhysicalFileSystem(), environment, GeneratorCache.DefaultRoot(environment), locks),
            new GenerationUpdate(new GeneratorRun(runner, environment), locks));
    }

    /// <inheritdoc />
    public async Task<CodegenResult> RunAsync(CodegenRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (request.Inputs.Count == 0)
        {
            return new CodegenResult([], []);
        }
        if (request.DesignTime)
        {
            return DesignTime(request);
        }

        var plan = await _preflight.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        if (plan.Problems.Count > 0)
        {
            return new CodegenResult([], plan.Problems.Select(p => Message(request, DamlDiagnosticSeverity.Error, p.Code, p.Message)).ToList());
        }
        var result = await _update.RunAsync(request, plan, cancellationToken).ConfigureAwait(false);
        return new CodegenResult(result.Files, result.Messages.Concat(Collisions(request, result.Files)).ToList());
    }

    private static CodegenResult DesignTime(CodegenRequest request)
    {
        var root = request.Codegen.GeneratedRoot ?? throw new ArgumentException("The request has no usable generation root.", nameof(request));
        var publisher = new GenerationPublisher(root);
        var files = new List<GeneratedFile>();
        var messages = new List<BuildMessage>();
        void Tell(string text) => messages.Add(Message(request, DamlDiagnosticSeverity.Information, null, text));

        foreach (var reference in request.Inputs.Select(i => i.Reference))
        {
            var folder = Path.Combine(root, reference.FolderName);
            GenerationManifest? manifest;
            try
            {
                var unfinished = publisher.HasUnfinishedPublish(reference.FolderName);
                if (unfinished)
                {
                    Tell($"A publish of the C# for {reference.Include} was left unfinished; build the project to finish it.");
                }
                manifest = GenerationManifest.Read(Path.Combine(folder, GenerationManifest.FileName));
                if (manifest is null && !unfinished)
                {
                    Tell($"No C# has been generated for {reference.Include} yet; build the project to generate it.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Tell($"The C# generated for {reference.Include} couldn't be read ({ex.Message}); build the project to see why.");
                continue;
            }
            if (manifest is null)
            {
                continue;
            }
            if (!DamlPath.Comparer.Equals(manifest.Owner, request.ProjectPath))
            {
                Tell($"{folder} holds C# generated for {manifest.Owner}, not for this project; build the project to see what to do.");
                continue;
            }
            files.AddRange(manifest.Files.Select(f => new GeneratedFile(Path.Combine(folder, f.Path), reference.GenerationId)));
        }
        return new CodegenResult(files, messages);
    }

    // Two generations that write the same file define the same types; the compiler would only say CS0101.
    private static IEnumerable<BuildMessage> Collisions(CodegenRequest request, IReadOnlyList<GeneratedFile> files)
    {
        var root = request.Codegen.GeneratedRoot!;
        var references = new Dictionary<string, GenerationReference>(StringComparer.Ordinal);
        foreach (var input in request.Inputs)
        {
            if (!references.ContainsKey(input.Reference.GenerationId))
            {
                references.Add(input.Reference.GenerationId, input.Reference);
            }
        }
        var owners = new Dictionary<string, GenerationReference>(StringComparer.OrdinalIgnoreCase);
        var clashes = new List<(GenerationReference First, GenerationReference Second, List<string> Files)>();
        foreach (var file in files)
        {
            if (!references.TryGetValue(file.GenerationId, out var reference))
            {
                continue;
            }
            var relative = file.Path.Substring(Path.Combine(root, reference.FolderName).Length + 1);
            if (!owners.TryGetValue(relative, out var first))
            {
                owners.Add(relative, reference);
                continue;
            }
            var clash = clashes.FirstOrDefault(c => c.First == first && c.Second == reference);
            if (clash.Files is null)
            {
                clash = (first, reference, []);
                clashes.Add(clash);
            }
            clash.Files.Add(relative);
        }
        return clashes.Select(c => Message(
            request,
            DamlDiagnosticSeverity.Error,
            DamlDiagnosticCodes.TypeCollision,
            $"{c.First.Include} and {c.Second.Include} generate the same C# ({string.Join(", ", c.Files.Take(ListedFiles))}" +
            $"{(c.Files.Count > ListedFiles ? $" and {c.Files.Count - ListedFiles} more" : "")}), so every type in it would be there twice. " +
            "Give one of the references a different Namespace."));
    }

    private static BuildMessage Message(CodegenRequest request, DamlDiagnosticSeverity severity, string? code, string text) =>
        new(severity, code, null, request.ProjectPath, 0, 0, 0, 0, text);
}
