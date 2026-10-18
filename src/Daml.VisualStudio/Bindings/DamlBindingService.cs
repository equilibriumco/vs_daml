using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.FileSystem;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Daml.VisualStudio.Bindings;

/// <summary>
/// The Daml binding index of each Roslyn workspace, started the first time a C# file of a
/// solution that compiles against the Daml runtime asks for it, and kept up to date from then on,
/// read whole again after every build. A solution without the Daml runtime gets no index and costs
/// a look at its project references. The log gets a line whenever what an index holds changes.
/// </summary>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class DamlBindingService : IDisposable
{
    private static readonly object WithoutDaml = new();

    private readonly IDamlLogger _logger;
    private readonly GenerationCatalog _catalog = new();
    private readonly Dictionary<Workspace, WorkspaceBindingIndex> _indexes = [];
    private readonly ConditionalWeakTable<Solution, object> _withoutDaml = new();
    private readonly object _gate = new();
    private string? _described;
    private bool _disposed;

    [ImportingConstructor]
    public DamlBindingService(IDamlLogger logger, JoinableTaskContext joinableTaskContext)
    {
        _logger = logger;
        Sources = new DamlSources();
        Locator = new DamlDefinitionLocator(new PhysicalFileSystem());
        // A build swaps generation folders whole, and the workspace may never report their files changed.
        var factory = joinableTaskContext.Factory;
        factory.RunAsync(async () =>
        {
            await factory.SwitchToMainThreadAsync();
            Community.VisualStudio.Toolkit.VS.Events.BuildEvents.SolutionBuildDone += _ => ReadAgain();
        }).FileAndForget("daml/bindings/buildDone");
    }

    /// <summary>Whether the sources of a generation are still the Daml on this machine.</summary>
    public DamlSources Sources { get; }

    /// <summary>Which generation a generated file belongs to, shared with the indexes.</summary>
    public GenerationCatalog Catalog => _catalog;

    /// <summary>Raised, on any thread, when the index of a workspace has a new version.</summary>
    public event EventHandler? IndexChanged;

    /// <summary>Finds declarations in Daml sources.</summary>
    public DamlDefinitionLocator Locator { get; }

    /// <summary>
    /// The latest index of <paramref name="workspace"/>, starting it if the solution compiles against
    /// the Daml runtime; null when it doesn't. A new index is empty until its first read ends.
    /// </summary>
    public BindingIndex? IndexOf(Workspace workspace)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }
            if (_indexes.TryGetValue(workspace, out var index))
            {
                return index.Current;
            }
            var solution = workspace.CurrentSolution;
            if (_withoutDaml.TryGetValue(solution, out _))
            {
                return null;
            }
            if (!solution.Projects.Any(BindingIndexBuilder.ReferencesDamlRuntime))
            {
                _withoutDaml.Add(solution, WithoutDaml);
                return null;
            }
            index = new WorkspaceBindingIndex(workspace, _catalog, TimeProvider.System, TimeSpan.FromSeconds(1), _logger);
            index.Published += (_, published) =>
            {
                Describe(published);
                IndexChanged?.Invoke(this, EventArgs.Empty);
            };
            _indexes.Add(workspace, index);
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, "Reading the Daml bindings of the solution's C# projects.");
            return index.Current;
        }
    }

    /// <summary>What the C# at <paramref name="position"/> stands for in Daml, with the index that says so; null for nothing.</summary>
    public async Task<(BindingsAtPosition Found, BindingIndex Index)?> FindAsync(Document document, int position, CancellationToken cancellationToken)
    {
        if (IndexOf(document.Project.Solution.Workspace) is not { } index)
        {
            return null;
        }
        return await DamlBindingLookup.FindAsync(document, position, index, cancellationToken).ConfigureAwait(false) is { } found ? (found, index) : null;
    }

    /// <summary>Reads every followed solution again, as after a build or a background update of the bindings.</summary>
    public void ReadAgain()
    {
        List<WorkspaceBindingIndex> indexes;
        lock (_gate)
        {
            indexes = [.. _indexes.Values];
        }
        foreach (var index in indexes)
        {
            index.ReadAgain();
        }
    }

    // Says why a lens or a command went away, or came back.
    private void Describe(BindingIndex index)
    {
        var description = index.Describe();
        lock (_gate)
        {
            if (description == _described)
            {
                return;
            }
            _described = description;
        }
        _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Daml bindings of the solution: {description}.");
    }

    /// <summary>Stops following the workspaces.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var index in _indexes.Values)
            {
                index.Dispose();
            }
            _indexes.Clear();
        }
    }
}
