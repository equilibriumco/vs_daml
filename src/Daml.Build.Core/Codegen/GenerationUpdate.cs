using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Building;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Locking;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// Brings the generations a preflight planned up to date, under the lock of their root so that
/// no two builds publish into it at once. The preflight judged them before the lock, so under it
/// each one is first recovered from whatever an earlier build left half done and then judged
/// again: another build may have published it in the meantime. Only a generation still stale
/// runs the generator, into a staging folder that is published whole or not at all.
/// </summary>
public sealed class GenerationUpdate
{
    private const int ListedFiles = 5;

    private readonly GeneratorRun _run;
    private readonly string _locks;

    /// <summary>Creates the update.</summary>
    /// <param name="run">Runs the generator.</param>
    /// <param name="locks">The folder of the build's locks, usually <see cref="LockNames.Root"/>.</param>
    public GenerationUpdate(GeneratorRun run, string locks)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
    }

    /// <summary>Updates the generations of <paramref name="plan"/> and lists the files to compile.</summary>
    /// <param name="request">The real build the plan was made for.</param>
    /// <param name="plan">A plan without problems.</param>
    /// <param name="cancellationToken">
    /// Stops the wait or the generator; generations published before stay, the one being
    /// generated keeps its previous version.
    /// </param>
    /// <returns>
    /// The files of every generation that is current at the end, and what happened; a generation
    /// that couldn't be brought up to date has an error and no files.
    /// </returns>
    /// <exception cref="OperationCanceledException">The update was cancelled.</exception>
    public async Task<CodegenResult> RunAsync(CodegenRequest request, CodegenPlan plan, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }
        if (plan.Problems.Count > 0 || plan.Tools is not { } tools)
        {
            throw new ArgumentException("Only a plan without problems can be carried out.", nameof(plan));
        }
        var root = request.Codegen.GeneratedRoot ?? throw new ArgumentException("The request has no usable generation root.", nameof(request));
        var files = new List<GeneratedFile>();
        var messages = new List<BuildMessage>();
        void Report(DamlDiagnosticSeverity severity, string? code, string text) =>
            messages.Add(new BuildMessage(severity, code, null, request.ProjectPath, 0, 0, 0, 0, text));

        // Our own steps take any length; the compiler and the editor may not.
        void Collect(IReadOnlyList<string> generated, GenerationReference reference)
        {
            files.AddRange(generated.Select(f => new GeneratedFile(f, reference.GenerationId)));
            var longest = generated.OrderByDescending(f => f.Length).FirstOrDefault();
            if (longest is not null && longest.Length > LongPaths.MaxPathLength)
            {
                Report(DamlDiagnosticSeverity.Warning, DamlDiagnosticCodes.LongGeneratedPath,
                    $"C# generated for {reference.Include} has paths longer than the {LongPaths.MaxPathLength} characters Windows takes unless long paths are turned on; " +
                    $"the longest has {longest.Length.ToString(CultureInfo.InvariantCulture)}: {longest}. The compiler or Visual Studio may not open such files. " +
                    $"Set {DamlProperties.GeneratedDir.Name} to a shorter folder, or turn on long paths in Windows (LongPathsEnabled).");
            }
        }

        if (await LockAsync(request, root, Report, cancellationToken).ConfigureAwait(false) is not { } held)
        {
            return new CodegenResult(files, messages);
        }
        using (held)
        {
            var publisher = new GenerationPublisher(root);
            foreach (var planned in plan.Generations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = planned.Input.Reference;
                RecoveryOutcome recovered;
                try
                {
                    recovered = publisher.Recover(reference.FolderName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report(DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.RollbackFailed,
                        $"An earlier build left the publish of {reference.FolderName} unfinished, and it couldn't be finished or undone ({ex.Message}). " +
                        $"Close whatever holds files under {root}, then build again.");
                    continue;
                }
                var leftUnfinished = recovered is RecoveryOutcome.RolledBack or RecoveryOutcome.Completed;
                if (leftUnfinished)
                {
                    Report(DamlDiagnosticSeverity.Information, null, recovered == RecoveryOutcome.RolledBack
                        ? $"An earlier build left the publish of {planned.Folder} unfinished; it was undone and the previous generation is back."
                        : $"An earlier build left the publish of {planned.Folder} unfinished; it was finished.");
                }

                var state = GenerationState.Evaluate(planned.Folder, planned.Fingerprint, request.ProjectPath);
                switch (state.Status)
                {
                    case GenerationStatus.Conflict:
                        Report(DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.OutputConflict, state.Reason!);
                        break;
                    case GenerationStatus.Current:
                        if (planned.State.Status != GenerationStatus.Current && !leftUnfinished)
                        {
                            Report(DamlDiagnosticSeverity.Information, null,
                                $"C# for {reference.Include} in {planned.Folder} was brought up to date by another build while this one waited.");
                        }
                        Collect(state.Files, reference);
                        break;
                    default:
                        if (await GenerateAsync(publisher, tools, planned, request, cancellationToken).ConfigureAwait(false) is { } problem)
                        {
                            Report(DamlDiagnosticSeverity.Error, problem.Code, problem.Message);
                            break;
                        }
                        Report(DamlDiagnosticSeverity.Information, null, $"Generated C# for {reference.Include} into {planned.Folder}. {state.Reason}");
                        var manifest = GenerationManifest.Read(Path.Combine(planned.Folder, GenerationManifest.FileName))!;
                        Collect(manifest.Files.Select(f => Path.Combine(planned.Folder, f.Path)).ToList(), reference);
                        break;
                }
            }
            Sweep(publisher, root, plan.Generations.Select(g => g.Input.Reference.FolderName).ToList(), request.ProjectPath, Report);
        }
        return new CodegenResult(files, messages);
    }

    /// <summary>
    /// Removes every generation of the request's project under its root, then the work folder
    /// and the root when nothing else is left in them: what Clean does, and what a real build does
    /// once the project has no references left. Files the generator didn't write stay.
    /// </summary>
    /// <param name="request">The project and its generation root.</param>
    /// <param name="cancellationToken">Stops the wait for the root's lock.</param>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<CodegenResult> CleanAsync(CodegenRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var root = request.Codegen.GeneratedRoot ?? throw new ArgumentException("The request has no usable generation root.", nameof(request));
        var messages = new List<BuildMessage>();
        void Report(DamlDiagnosticSeverity severity, string? code, string text) =>
            messages.Add(new BuildMessage(severity, code, null, request.ProjectPath, 0, 0, 0, 0, text));
        if (!Directory.Exists(root))
        {
            return new CodegenResult([], messages);
        }
        if (await LockAsync(request, root, Report, cancellationToken).ConfigureAwait(false) is { } held)
        {
            using (held)
            {
                Sweep(new GenerationPublisher(root), root, [], request.ProjectPath, Report);
            }
        }
        return new CodegenResult([], messages);
    }

    private async Task<FileLock?> LockAsync(
        CodegenRequest request,
        string root,
        Action<DamlDiagnosticSeverity, string?, string> report,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FileLock.AcquireAsync(LockNames.Output(_locks, root), request.Tools.LockTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            report(DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.LockTimeout,
                $"Another build kept the generated C# in {root} locked for longer than {request.Tools.LockTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s. " +
                $"Wait for it to finish, or raise {DamlProperties.LockTimeoutSeconds.Name}.");
            return null;
        }
    }

    // Generations of references the project no longer has, and whatever their runs left in the
    // work folder; another project's generation is its own build's business. Nothing here fails
    // the build: none of it is compiled, and the next build tries again.
    private static void Sweep(
        GenerationPublisher publisher,
        string root,
        IReadOnlyCollection<string> current,
        string owner,
        Action<DamlDiagnosticSeverity, string?, string> report)
    {
        var keep = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        var keepIds = new HashSet<string>(current.Select(GenerationIdentity.IdOfFolder), StringComparer.Ordinal);
        void Failed(string what, Exception ex) =>
            report(DamlDiagnosticSeverity.Warning, null, $"{what} couldn't be removed ({ex.Message}); the next build tries again.");

        foreach (var folderName in publisher.FolderNames().Where(name => !keep.Contains(name)))
        {
            var folder = Path.Combine(root, folderName);
            try
            {
                var removal = publisher.Remove(folderName, owner);
                if (!removal.Owned)
                {
                    continue;
                }
                report(DamlDiagnosticSeverity.Information, null, removal.KeptFiles.Count == 0
                    ? $"Removed the generated C# in {folder}."
                    : $"Removed the generated C# in {folder}, but kept what the generator didn't write there: {string.Join(", ", removal.KeptFiles.Take(ListedFiles))}" +
                      $"{(removal.KeptFiles.Count > ListedFiles ? ", …" : "")}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failed($"The generated C# in {folder}", ex);
            }
        }
        try
        {
            foreach (var id in publisher.WorkIds().Where(id => !keepIds.Contains(id)))
            {
                publisher.RemoveWork(id);
            }
            publisher.RemoveIfEmpty();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failed($"What earlier builds left in {Path.Combine(root, GenerationPublisher.WorkFolderName)}", ex);
        }
    }

    private async Task<ConfigurationError?> GenerateAsync(
        GenerationPublisher publisher,
        CodegenTools tools,
        PlannedGeneration planned,
        CodegenRequest request,
        CancellationToken cancellationToken)
    {
        var folderName = planned.Input.Reference.FolderName;
        string staging;
        try
        {
            staging = publisher.NewStaging(folderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Most often a root too long for this host to make folders in.
            return new ConfigurationError(
                DamlDiagnosticCodes.CodegenFailed,
                $"The generator's work folder {Path.Combine(Path.GetDirectoryName(planned.Folder)!, GenerationPublisher.WorkFolderName)} couldn't be made ready ({ex.Message}). " +
                $"If the path is long, a shorter {DamlProperties.GeneratedDir.Name} or long paths enabled in Windows helps.");
        }
        try
        {
            if (await _run.RunAsync(tools, planned, staging, request.ProjectPath, request.Tools.ProcessTimeout, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return problem;
            }
            publisher.Publish(folderName, staging);
            return null;
        }
        catch (GenerationPublishException ex)
        {
            return new ConfigurationError(ex.Code, ex.Message);
        }
        finally
        {
            Discard(publisher, staging);
        }
    }

    // Whatever stays is an orphan the next recovery removes.
    private static void Discard(GenerationPublisher publisher, string staging)
    {
        try
        {
            publisher.Discard(staging);
        }
        catch (IOException)
        {
            // See above.
        }
    }
}
