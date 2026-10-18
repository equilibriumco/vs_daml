using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Roslyn.Tests.Bindings;

// The index following a live AdhocWorkspace, on the real clock with a short debounce.
public sealed class WorkspaceBindingIndexTests : IDisposable
{
    private const string Package = GeneratedCode.KindsPackage;
    private const string Other = "1c7a18f31befd3df5426c623abcbe71fcfe796665de0feefd09ce752ef7fa9fb";
    private const string Id = "9de80d6cfe50bada";

    private static readonly DamlSymbolId Asset = new(Package, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template);
    private static readonly DamlSymbolId Point = new(Package, "Kinds.Types", "Point", null, DamlSymbolKind.Record);

    private readonly GenerationsOnDisk _disk = new();
    private readonly TestSolution _solution = new();
    private readonly GenerationCatalog _catalog = new();
    private readonly RecordingLogger _logger = new();
    private WorkspaceBindingIndex? _index;

    public void Dispose()
    {
        _index?.Dispose();
        _solution.Dispose();
        _disk.Dispose();
    }

    private WorkspaceBindingIndex Follow() =>
        _index = new WorkspaceBindingIndex(_solution.Workspace, _catalog, TimeProvider.System, TimeSpan.FromMilliseconds(50), _logger);

    private async Task<BindingIndex> Until(Func<BindingIndex, bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (condition(_index!.Current))
            {
                return _index.Current;
            }
            await Task.Delay(20);
        }
        _logger.Entries.ShouldBeEmpty();
        throw new TimeoutException($"The index never got there; it is at version {_index!.Current.Version} with {_index.Current.Count} declarations.");
    }

    private ProjectId OpenApp()
    {
        var app = _solution.Add("App", _disk.Publish("App", id: Id));
        _solution.Apply();
        return app;
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_solution_there_when_it_starts_is_read()
    {
        OpenApp();

        Follow();

        (await Until(i => i.LocationsOf(Asset).Count == 1)).LocationsOf(Point).ShouldHaveSingleItem();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_project_opened_later_is_read_and_one_closed_is_dropped()
    {
        Follow();
        var app = OpenApp();
        await Until(i => i.LocationsOf(Asset).Count == 1);

        _solution.Change(s => s.RemoveProject(app));
        _solution.Apply();

        await Until(i => i.Count == 0);
    }

    // Generated code edited in the editor, without a build: the edit is what the index sees.
    [Fact, Trait("Category", "Unit")]
    public async Task An_edited_document_is_read_again()
    {
        var app = OpenApp();
        Follow();
        await Until(i => i.LocationsOf(Asset).Count == 1);
        var asset = _solution.Solution.GetProject(app)!.Documents.Single(d => d.FilePath!.EndsWith(@"\Asset\Asset.cs", StringComparison.Ordinal));

        _solution.Change(s => s.WithDocumentText(asset.Id, SourceText.From("namespace Fixture.Kinds.Asset;")));
        _solution.Apply();

        (await Until(i => i.LocationsOf(Asset).Count == 0)).LocationsOf(Point).ShouldHaveSingleItem();
    }

    // A new DAR version: the build publishes the generation again with the new package, and the
    // editor reloads the files it rewrote.
    [Fact, Trait("Category", "Unit")]
    public async Task A_DAR_upgrade_moves_every_binding_to_the_new_package()
    {
        var app = OpenApp();
        Follow();
        await Until(i => i.LocationsOf(Asset).Count == 1);

        var upgraded = _disk.Publish("App", package: Other, id: Id);
        _solution.Change(s =>
        {
            var project = s.GetProject(app)!;
            foreach (var document in project.Documents)
            {
                s = s.RemoveDocument(document.Id);
            }
            foreach (var (path, text) in upgraded)
            {
                s = s.AddDocument(DocumentId.CreateNewId(app), Path.GetFileName(path), SourceText.From(text), filePath: path);
            }
            return s;
        });
        _solution.Apply();

        var index = await Until(i => i.LocationsOf(new DamlSymbolId(Other, "Kinds.Asset", "Asset", null, DamlSymbolKind.Template)).Count == 1);
        index.LocationsOf(Asset).ShouldBeEmpty();
        index.LocationsOf(Point).ShouldBeEmpty();
        index.LocationsOf(new DamlSymbolId(Other, "Kinds.Types", "Point", null, DamlSymbolKind.Record)).ShouldHaveSingleItem();
    }

    // Republished without any file changing, so only whoever watches the folder can tell.
    [Fact, Trait("Category", "Unit")]
    public async Task A_republished_manifest_reported_by_the_host_is_read_again()
    {
        OpenApp();
        Follow();
        await Until(i => i.LocationsOf(Asset).Count == 1);
        var folder = Path.GetDirectoryName(_index!.Current.LocationsOf(Asset).Single().Generation.Folder + @"\x")!;

        _disk.Publish("App", id: Id, recordPackage: false);
        _index.GenerationChanged(folder);

        await Until(i => i.Count == 0);
    }

    public static IEnumerable<object[]> Kinds() =>
    [
        [WorkspaceChangeKind.SolutionAdded, "all"],
        [WorkspaceChangeKind.SolutionReloaded, "all"],
        [WorkspaceChangeKind.ProjectAdded, "project"],
        [WorkspaceChangeKind.ProjectChanged, "project"],
        [WorkspaceChangeKind.ProjectRemoved, "removed"],
        [WorkspaceChangeKind.DocumentAdded, "document"],
        [WorkspaceChangeKind.DocumentChanged, "document"],
        [WorkspaceChangeKind.DocumentRemoved, "document"],
        [WorkspaceChangeKind.AdditionalDocumentChanged, "none"],
        [WorkspaceChangeKind.AnalyzerConfigDocumentChanged, "none"],
    ];

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(Kinds))]
    public void Each_kind_of_workspace_change_asks_for_what_it_touched(WorkspaceChangeKind kind, string expected)
    {
        var app = _solution.Add("App", [(@"C:\work\App\Program.cs", "internal static class Program { }")]);
        var solution = _solution.Solution;
        var document = solution.GetProject(app)!.Documents.Single().Id;

        var changes = WorkspaceBindingIndex.ChangesOf(new WorkspaceChangeEventArgs(kind, solution, solution, app, document));

        var key = BindingIndexBuilder.ProjectKey(app);
        (expected switch
        {
            "all" => changes.All,
            "project" => changes.Projects.SequenceEqual([key]),
            "removed" => changes.RemovedProjects.SequenceEqual([key]),
            "document" => changes.Documents.SequenceEqual([(key, @"C:\work\App\Program.cs")]),
            _ => changes.IsEmpty,
        }).ShouldBeTrue(kind.ToString());
    }
}
