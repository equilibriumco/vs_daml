using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Manifests;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.ScriptResults;
using Daml.VisualStudio.LanguageServer.Lenses;
using Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Daml.VisualStudio.Bindings;

/// <summary>
/// C# references for the lenses of Daml files, from the solution's Roslyn workspace and its binding
/// index: a Daml file's package is linked to the generations built from its <c>daml.yaml</c>, current
/// while their recorded sources are the files on disk. Edits to the C# or a new index make every
/// count out of date, once things are quiet for a moment.
/// </summary>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class RoslynCSharpReferenceSource : ICSharpReferenceSource, IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    // Hashing a package's sources isn't free, and the lenses ask on every scroll; a save shows within this.
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromSeconds(2);

    private readonly VisualStudioWorkspace _workspace;
    private readonly DamlBindingService _bindings;
    private readonly IDamlLogger _logger;
    private readonly WorkspaceLocator _locator;
    private readonly Debouncer _quiet = new(Quiet, TimeProvider.System);
    private readonly Dictionary<string, (DateTime At, Link Link)> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    [ImportingConstructor]
    public RoslynCSharpReferenceSource(VisualStudioWorkspace workspace, DamlBindingService bindings, IDamlLogger logger, JoinableTaskContext joinableTaskContext)
    {
        _workspace = workspace;
        _bindings = bindings;
        _logger = logger;
        var files = new PhysicalFileSystem();
        _locator = new WorkspaceLocator(files, new YamlManifestReader(files));
        _workspace.WorkspaceChanged += OnWorkspaceChanged;
        _bindings.IndexChanged += OnIndexChanged;
        // A saved Daml file can make the bindings older than the sources, with nothing else telling.
        var factory = joinableTaskContext.Factory;
        factory.RunAsync(async () =>
        {
            await factory.SwitchToMainThreadAsync();
            VS.Events.DocumentEvents.Saved += OnSaved;
        }).FileAndForget("daml/csharpReferences/saved");
    }

    public event EventHandler? Changed;

    public CSharpLinkState LinkOf(string damlFile) => LinkFor(damlFile).State;

    public async Task<IReadOnlyList<CSharpReference>?> FindAsync(string damlFile, string damlModule, DamlLensDeclaration declaration, CancellationToken cancellationToken)
    {
        if (_bindings.IndexOf(_workspace) is not { } index)
        {
            return null;
        }
        var locations = LinkFor(damlFile).Packages
            .SelectMany(package => index.LocationsOf(new DamlSymbolId(package, damlModule, declaration.Entity, declaration.Member, declaration.Kind)))
            .ToList();
        if (locations.Count == 0)
        {
            return null;
        }
        var clock = Stopwatch.StartNew();
        var references = await CSharpReferenceFinder.FindAsync(_workspace.CurrentSolution, locations, _bindings.Catalog, cancellationToken).ConfigureAwait(false);
        _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information,
            $"{references.Count} C# reference(s) of {damlModule}:{declaration.Name} ({clock.ElapsedMilliseconds} ms).");
        return references;
    }

    public void Dispose()
    {
        _workspace.WorkspaceChanged -= OnWorkspaceChanged;
        _bindings.IndexChanged -= OnIndexChanged;
        _quiet.Dispose();
    }

    private void OnSaved(string? path)
    {
        if (path is not null && (path.EndsWith(".daml", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("daml.yaml", StringComparison.OrdinalIgnoreCase)))
        {
            lock (_gate)
            {
                _links.Clear();
            }
            _ = ChangedWhenQuietAsync();
        }
    }

    private Link LinkFor(string damlFile)
    {
        if (_locator.FindForFile(damlFile).Package?.ManifestPath is not { } manifest)
        {
            return Link.None;
        }
        lock (_gate)
        {
            if (_links.TryGetValue(manifest, out var cached) && DateTime.UtcNow - cached.At < LinkLifetime)
            {
                return cached.Link;
            }
        }
        var generations = _bindings.IndexOf(_workspace)?.GenerationsFrom(manifest) ?? [];
        var statuses = generations.Select(g => (Generation: g, Status: _bindings.Sources.StatusOf(g))).ToList();
        var current = statuses.Where(s => s.Status == SourceStatus.Current).Select(s => s.Generation.MainPackageId).Distinct().ToList();
        var link = current.Count > 0 ? new Link(CSharpLinkState.Current, current)
            : statuses.Any(s => s.Status == SourceStatus.Stale) ? new Link(CSharpLinkState.Stale, [])
            : Link.None;
        lock (_gate)
        {
            _links[manifest] = (DateTime.UtcNow, link);
        }
        return link;
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs e) => _ = ChangedWhenQuietAsync();

    private void OnIndexChanged(object? sender, EventArgs e) => _ = ChangedWhenQuietAsync();

    private async Task ChangedWhenQuietAsync()
    {
        if (await _quiet.WaitAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                _links.Clear();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed record Link(CSharpLinkState State, IReadOnlyList<string> Packages)
    {
        public static Link None { get; } = new(CSharpLinkState.None, []);
    }
}
