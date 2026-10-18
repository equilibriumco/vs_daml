using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Core.Logging;
using Microsoft.Build.Evaluation;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.Threading;

namespace Daml.VisualStudio.Bindings;

/// <summary>
/// Has Visual Studio evaluate a C# project again when the list of its generated Daml files exists
/// but the project's last evaluation didn't import it. Visual Studio watches only the files an
/// evaluation imported, so a list that was missing then (a Clean of an older package deleted it,
/// obj was deleted, or nothing was built yet) goes unnoticed when a build writes it, and
/// IntelliSense lacks every binding until the project is reloaded. Looked at after builds and
/// background updates, and when the bindings of a solution are first read.
/// </summary>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class GeneratedListImports
{
    // Where the package keeps the list of a configuration and target framework, file or no file.
    private const string ListProperty = "_DamlGeneratedProps";

    private readonly IProjectServiceAccessor _projects;
    private readonly IDamlLogger _logger;

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
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or AggregateException)
                {
                    // Unloading or closing meanwhile; the next build looks again.
                    _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Debug, $"Couldn't look at the evaluation of {unconfigured.FullPath}.", ex);
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
        var missed = await locks.ReadLockAsync(
            access => access.GetProjectAsync(configured).ContinueWith(evaluation => MissedList(evaluation.Result), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default),
            CancellationToken.None).ConfigureAwait(false);
        if (missed is null)
        {
            return false;
        }
        _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information,
            $"{Path.GetFileName(unconfigured.FullPath)} ({configured.ProjectConfiguration.Name}) didn't see {Path.GetFileName(missed)}, written after it was last evaluated; evaluating it again so IntelliSense gets the generated C#.");
        // A dirty evaluation is redone when the lock is let go, and the new one imports the list.
        await locks.WriteLockAsync(
            access => access.GetProjectAsync(configured).ContinueWith(evaluation => evaluation.Result.MarkDirty(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default),
            CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    // MSBuild's types stay out of async methods: their state machines keep locals and awaiters in
    // fields, and the VSSDK's pkgdef step loads every type of the assembly without MSBuild at hand.
    private static string? MissedList(Project project)
    {
        var list = project.GetPropertyValue(ListProperty);
        return GeneratedListImport.Missed(list, project.Imports.Select(i => i.ImportedProject.FullPath), File.Exists) ? list : null;
    }
}
