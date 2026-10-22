using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Microsoft.Build.Evaluation;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.Threading;

namespace Equilibrium.Daml.VisualStudio.Bindings;

/// <summary>
/// Has Visual Studio evaluate a C# project again when the list of its generated Daml files exists
/// but the project's last evaluation didn't import it. Visual Studio watches only the files an
/// evaluation imported, so a list that was missing then (nothing was built yet, obj was deleted,
/// a Clean of an older package deleted it, or the package itself wasn't restored yet) goes
/// unnoticed when a build writes it, and IntelliSense lacks every binding until the project is
/// reloaded. Looked at after builds and background updates, and when the bindings of a solution
/// are first read; when evaluating again doesn't help, the log asks once for a reload. Visual
/// Studio's evaluations leave out private properties, so the list's path comes from public ones.
/// </summary>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class GeneratedListImports
{
    private readonly IProjectServiceAccessor _projects;
    private readonly IDamlLogger _logger;
    private readonly MissedLists _missed = new();

    [ImportingConstructor]
    public GeneratedListImports(IProjectServiceAccessor projects, IDamlLogger logger)
    {
        _projects = projects;
        _logger = logger;
    }

    /// <summary>Evaluates again every loaded project configuration that missed its list; returns how many there were.</summary>
    public async Task<int> CatchUpAsync()
    {
        await TaskScheduler.Default;
        var caughtUp = 0;
        foreach (var unconfigured in _projects.GetProjectService().LoadedUnconfiguredProjects.ToList())
        {
            foreach (var configured in unconfigured.LoadedConfiguredProjects.ToList())
            {
                try
                {
                    if (await CatchUpAsync(unconfigured, configured).ConfigureAwait(false))
                    {
                        caughtUp++;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Unloading or closing meanwhile, or the project system said no; the next build looks again.
                    _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Couldn't look at the evaluation of {unconfigured.FullPath} ({configured.ProjectConfiguration.Name}).", ex);
                }
            }
        }
        return caughtUp;
    }

    private async Task<bool> CatchUpAsync(UnconfiguredProject unconfigured, ConfiguredProject configured)
    {
        var locks = unconfigured.ProjectService.Services.ProjectLockService;
        if (locks is null)
        {
            return false;
        }
        var (list, missed) = await locks.ReadLockAsync(access => WithEvaluationAsync(access.GetProjectAsync, configured, ListOf), CancellationToken.None).ConfigureAwait(false);
        if (list is null)
        {
            return false;
        }
        var project = $"{Path.GetFileName(unconfigured.FullPath)} ({configured.ProjectConfiguration.Name})";
        switch (_missed.Next(list, missed))
        {
            case MissedListAction.EvaluateAgain:
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information,
                    $"{project} didn't see {Path.GetFileName(list)}, written after it was last evaluated; evaluating it again so IntelliSense gets the generated C#.");
                // Marked dirty under the write lock, the evaluation is redone when the lock is let go, and
                // the new one imports the list. The project file itself doesn't change.
                await locks.WriteLockAsync(access => WithEvaluationAsync(access.GetProjectAsync, configured, MarkDirty), CancellationToken.None).ConfigureAwait(false);
                return true;
            case MissedListAction.AskForReload:
                _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning,
                    $"{project} still doesn't see {Path.GetFileName(list)} after being evaluated again, so IntelliSense lacks the generated C#; reload the project (right-click it > Reload Project).");
                return false;
            default:
                return false;
        }
    }

    // Uses the evaluation under the lock the caller holds. It takes the lock's method rather than the
    // lock and holds the evaluation as a plain Task, so no type here has a field whose layout needs
    // MSBuild: the VSSDK's pkgdef step loads every type of the assembly without MSBuild at hand. It
    // stays on the lock's own context, away from which the project system no longer counts the lock
    // as held.
    private static async Task<T> WithEvaluationAsync<T>(Func<ConfiguredProject, CancellationToken, Task> evaluate, ConfiguredProject configured, Func<Task, T> use)
    {
        var evaluation = evaluate(configured, CancellationToken.None);
        await evaluation.ConfigureAwait(true);
        return use(evaluation);
    }

#pragma warning disable VSTHRD002 // awaited just before: the result is there
    private static (string? List, bool Missed) ListOf(Task evaluation)
    {
        var project = ((Task<Project>)evaluation).Result;
        var list = GeneratedListImport.ListPath(
            project.DirectoryPath,
            project.GetPropertyValue("BaseIntermediateOutputPath"),
            project.GetPropertyValue("MSBuildProjectFile"),
            project.GetPropertyValue("Configuration"),
            project.GetPropertyValue("TargetFramework"));
        return (list, GeneratedListImport.Missed(list, project.Imports.Select(i => i.ImportedProject.FullPath), File.Exists));
    }

    private static bool MarkDirty(Task evaluation)
    {
        ((Task<Project>)evaluation).Result.MarkDirty();
        return true;
    }
#pragma warning restore VSTHRD002
}
