using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Microsoft.CodeAnalysis;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Bindings;

/// <summary>
/// The binding index of a Roslyn workspace, kept up to date as it changes: the workspace's change
/// events become requests to read projects and documents again, and whoever watches the generation
/// folders or the builds reports a republished manifest or asks for everything again. Reads run off
/// the event's thread, on the workspace's current solution. Generated files left out of the index
/// are reported once for each reason.
/// </summary>
public sealed class WorkspaceBindingIndex : IDisposable
{
    private readonly Workspace _workspace;
    private readonly GenerationCatalog _catalog;
    private readonly IDamlLogger _logger;
    private readonly BindingIndexUpdates _updates;
    private readonly HashSet<string> _reported = [];

    /// <summary>Starts reading the workspace's whole solution, then follows its changes.</summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="catalog">The generations the files belong to.</param>
    /// <param name="time">The clock the debounce waits on.</param>
    /// <param name="debounce">How long the workspace has to be quiet before a read.</param>
    /// <param name="logger">Where failed reads, and generated files left out, are reported.</param>
    public WorkspaceBindingIndex(Workspace workspace, GenerationCatalog catalog, TimeProvider time, TimeSpan debounce, IDamlLogger logger)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _updates = new BindingIndexUpdates(ReadAsync, time, debounce, logger);
        _workspace.WorkspaceChanged += OnWorkspaceChanged;
        _updates.Request(IndexChanges.Everything);
    }

    /// <summary>Raised on a thread-pool thread with every new index.</summary>
    public event EventHandler<BindingIndex>? Published
    {
        add => _updates.Published += value;
        remove => _updates.Published -= value;
    }

    /// <summary>The latest index.</summary>
    public BindingIndex Current => _updates.Current;

    /// <summary>Asks for the generation in <paramref name="folder"/> to be read again, as after a build published it.</summary>
    public void GenerationChanged(string folder) => _updates.Request(IndexChanges.Generation(folder));

    /// <summary>
    /// Asks for the whole solution to be read again, as after a build: it may have published
    /// generations without the workspace reporting any of their files changed.
    /// </summary>
    public void ReadAgain() => _updates.Request(IndexChanges.Everything);

    /// <summary>Stops following the workspace.</summary>
    public void Dispose()
    {
        _workspace.WorkspaceChanged -= OnWorkspaceChanged;
        _updates.Dispose();
    }

    /// <summary>What a workspace change asks to be read again.</summary>
    public static IndexChanges ChangesOf(WorkspaceChangeEventArgs change)
    {
        string? Path(Solution solution) => change.DocumentId is { } id ? solution.GetDocument(id)?.FilePath : null;
        string Key() => BindingIndexBuilder.ProjectKey(change.ProjectId!);
        return (change ?? throw new ArgumentNullException(nameof(change))).Kind switch
        {
            WorkspaceChangeKind.SolutionAdded or WorkspaceChangeKind.SolutionChanged or WorkspaceChangeKind.SolutionReloaded
                or WorkspaceChangeKind.SolutionCleared or WorkspaceChangeKind.SolutionRemoved => IndexChanges.Everything,
            WorkspaceChangeKind.ProjectAdded or WorkspaceChangeKind.ProjectChanged or WorkspaceChangeKind.ProjectReloaded => IndexChanges.Project(Key()),
            WorkspaceChangeKind.ProjectRemoved => IndexChanges.RemovedProject(Key()),
            WorkspaceChangeKind.DocumentRemoved => Path(change.OldSolution) is { } removed ? IndexChanges.Document(Key(), removed) : IndexChanges.None,
            WorkspaceChangeKind.DocumentAdded or WorkspaceChangeKind.DocumentChanged or WorkspaceChangeKind.DocumentReloaded
                or WorkspaceChangeKind.DocumentInfoChanged => Path(change.NewSolution) is { } path ? IndexChanges.Document(Key(), path) : IndexChanges.None,
            _ => IndexChanges.None,
        };
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs change) => _updates.Request(ChangesOf(change));

    private async Task<BindingIndex> ReadAsync(BindingIndex index, IndexChanges changes, CancellationToken cancellationToken)
    {
        var leftOut = new List<(string Path, string Problem)>();
        var read = await ReadAsync(index, changes, (path, problem) => leftOut.Add((path, problem)), cancellationToken).ConfigureAwait(false);
        Report(leftOut);
        return read;
    }

    private async Task<BindingIndex> ReadAsync(BindingIndex index, IndexChanges changes, Action<string, string> leftOut, CancellationToken cancellationToken)
    {
        var solution = _workspace.CurrentSolution;
        foreach (var folder in changes.Generations)
        {
            _catalog.Forget(folder);
        }
        if (changes.All)
        {
            index = index.Cleared();
            foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
            {
                index = await BindingIndexBuilder.WithProjectAsync(index, project, _catalog, leftOut, cancellationToken).ConfigureAwait(false);
            }
            return index;
        }
        foreach (var removed in changes.RemovedProjects)
        {
            index = index.WithoutProject(removed);
        }
        foreach (var key in changes.Projects)
        {
            index = BindingIndexBuilder.Project(solution, key) is { Language: LanguageNames.CSharp } project
                ? await BindingIndexBuilder.WithProjectAsync(index, project, _catalog, leftOut, cancellationToken).ConfigureAwait(false)
                : index.WithoutProject(key);
        }
        var documents = new List<Document>();
        foreach (var (key, path) in changes.Documents)
        {
            if (BindingIndexBuilder.Project(solution, key)?.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } document)
            {
                documents.Add(document);
            }
            else
            {
                index = index.WithoutDocument(key, path);
            }
        }
        foreach (var folder in changes.Generations)
        {
            var prefix = folder.TrimEnd('\\') + Path.DirectorySeparatorChar;
            documents.AddRange(solution.Projects.SelectMany(p => p.Documents)
                .Where(d => d.FilePath is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }
        return await BindingIndexBuilder.WithDocumentsAsync(index, documents.Distinct(), _catalog, leftOut, cancellationToken).ConfigureAwait(false);
    }

    // A generation the editor hasn't caught up with leaves out every file for the same reason, and
    // reads come again with every change: one line per generation, once for each reason.
    private void Report(List<(string Path, string Problem)> leftOut)
    {
        foreach (var generation in leftOut.GroupBy(l => _catalog.Find(l.Path)?.Folder ?? Path.GetDirectoryName(l.Path), StringComparer.OrdinalIgnoreCase))
        {
            var (path, problem) = generation.First();
            lock (_reported)
            {
                if (!_reported.Add(generation.Key.ToUpperInvariant() + "\n" + problem))
                {
                    continue;
                }
            }
            var files = generation.Select(l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning,
                $"The Daml bindings leave out {files} generated file{(files == 1 ? "" : "s")} of {generation.Key}, such as {Path.GetFileName(path)}: {problem}");
        }
    }
}
