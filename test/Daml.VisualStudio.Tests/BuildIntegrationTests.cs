using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core;
using Daml.Build.Core.Diagnostics;
using Daml.VisualStudio.Build;
using Daml.VisualStudio.Core.Build;
using Daml.VisualStudio.Core.Project;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;
using Microsoft.VisualStudio.Workspace.Indexing;
using Microsoft.VisualStudio.Workspace.Extensions.VS;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

// The Open Folder build parts outside the IDE: how VS discovers them, which contexts and
// actions they hand out, and what the Error List gets to see.
public class BuildIntegrationTests
{
    private static readonly string[] OurContextTypes =
    [
        BuildContextTypes.BuildContextType,
        BuildContextTypes.CleanContextType,
        BuildIds.TestContextTypeString,
    ];

    [Fact, Trait("Category", "Editor")]
    public async Task The_build_parts_are_discovered_without_errors()
    {
        var parts = await DiscoverAsync();

        parts.DiscoveryErrors.ShouldBeEmpty();
        PartsExporting(parts, typeof(IWorkspaceProviderFactory<IFileContextProvider>)).Count.ShouldBe(1);
        PartsExporting(parts, typeof(IWorkspaceProviderFactory<IFileContextActionProvider>)).Count.ShouldBe(1);
        PartsExporting(parts, typeof(IWorkspaceProviderFactory<IFileScanner>)).Count.ShouldBe(1);
        PartsExporting(parts, typeof(DamlBuildService)).ShouldHaveSingleItem().IsShared.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Editor")]
    public async Task The_scanner_indexes_both_manifests_for_build_configurations()
    {
        var metadata = Flatten(Exports(await DiscoverAsync(), typeof(IWorkspaceProviderFactory<IFileScanner>)).Single().Metadata.Values).ToList();

        // The attribute turns the names into suffix globs.
        metadata.ShouldContain("*" + WorkspaceLocator.PackageManifestName);
        metadata.ShouldContain("*" + WorkspaceLocator.MultiPackageManifestName);
        metadata.ShouldContain(typeof(IReadOnlyCollection<FileDataValue>));
    }

    [Theory, Trait("Category", "Editor")]
    [InlineData(@"C:\repo\app\daml.yaml", 1)]
    [InlineData(@"C:\repo\multi-package.yaml", 1)]
    [InlineData(@"C:\repo\app\not-daml.yaml", 0)]
    public async Task Scanned_manifests_carry_the_build_configuration_their_contexts_use(string path, int expected)
    {
        var scanner = new DamlBuildFileScannerFactory().CreateProvider(null!);

        var values = await scanner.ScanContentAsync<IReadOnlyCollection<FileDataValue>>(path, CancellationToken.None);

        values.Count.ShouldBe(expected);
        foreach (var value in values)
        {
            value.Type.ShouldBe(BuildConfigurationContext.ContextTypeGuid);
            value.Name.ShouldBe(BuildConfigurationContext.DataValueName);
            value.Context.ShouldBeNull();
        }
        var contexts = await new DamlFileContextProviderFactory(NoRun).CreateProvider(null!).GetContextsForFileAsync(path, CancellationToken.None);
        contexts.Select(c => ((IBuildConfigurationContext)c.Context).BuildConfiguration).ShouldAllBe(configuration => values.All(v => v.Context == configuration));
    }

    [Fact, Trait("Category", "Editor")]
    public async Task The_scanner_only_answers_for_file_data_values()
    {
        var scanner = new DamlBuildFileScannerFactory().CreateProvider(null!);

        await Should.ThrowAsync<NotImplementedException>(() => scanner.ScanContentAsync<IReadOnlyCollection<FileReferenceInfo>>(@"C:\repo\daml.yaml", CancellationToken.None));
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Contexts_cover_build_clean_and_test_while_actions_only_cover_test()
    {
        var parts = await DiscoverAsync();

        // The attributes take strings; the metadata holds them as Guids.
        var contextTypes = Flatten(Exports(parts, typeof(IWorkspaceProviderFactory<IFileContextProvider>)).Single().Metadata.Values).OfType<Guid>().ToList();
        foreach (var type in OurContextTypes)
        {
            contextTypes.ShouldContain(new Guid(type));
        }

        // Build and Clean run through the folder view's own commands; an action of ours mapped
        // onto them as well put a second Build in the menu.
        var actionTypes = Flatten(Exports(parts, typeof(IWorkspaceProviderFactory<IFileContextActionProvider>)).Single().Metadata.Values).OfType<Guid>().ToList();
        actionTypes.ShouldContain(BuildIds.TestContextType);
        actionTypes.ShouldNotContain(BuildContextTypes.BuildContextTypeGuid);
        actionTypes.ShouldNotContain(BuildContextTypes.CleanContextTypeGuid);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task The_edit_listener_watches_daml_documents()
    {
        var export = Exports(await DiscoverAsync(), typeof(IWpfTextViewCreationListener))
            .Single(e => Flatten(e.Metadata.Values).Contains("daml"));

        Flatten(export.Metadata.Values).ShouldContain(PredefinedTextViewRoles.Document);
    }

    [Theory, Trait("Category", "Editor")]
    [InlineData(@"C:\repo\app\daml.yaml", 3)]
    [InlineData(@"C:\repo\multi-package.yaml", 2)]
    [InlineData(@"C:\repo\app\daml\Main.daml", 0)]
    public async Task Manifests_get_one_file_context_per_command(string path, int expected)
    {
        var provider = new DamlFileContextProviderFactory(NoRun).CreateProvider(null!);

        var contexts = await provider.GetContextsForFileAsync(path, CancellationToken.None);

        contexts.Count.ShouldBe(expected);
        // Open Folder only enables Build when the context carries a build configuration that
        // matches the indexed one; ours is "not applicable" (null) on both sides.
        contexts.ShouldAllBe(c => c.ProviderType == BuildIds.FileContextProvider
            && c.Context is DamlBuildConfigurationContext && ((IBuildConfigurationContext)c.Context).BuildConfiguration == null);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Each_command_maps_to_its_context_type()
    {
        var contexts = await new DamlFileContextProviderFactory(NoRun).CreateProvider(null!).GetContextsForFileAsync(@"C:\repo\app\daml.yaml", CancellationToken.None);

        contexts.Select(c => c.ContextType).ShouldBe([BuildContextTypes.BuildContextTypeGuid, BuildIds.TestContextType, BuildContextTypes.CleanContextTypeGuid]);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task Test_answers_our_menu_command_and_nothing_else_gets_an_action()
    {
        var ran = new List<DamlBuildAction>();
        var factory = new DamlFileContextActionProviderFactory((action, _) => { ran.Add(action); return Task.FromResult(true); });
        var provider = factory.CreateProvider(null!);
        var test = Context(BuildIds.TestContextType, DpmCommandKind.Test, @"C:\repo\app", false, @"C:\repo\app\daml.yaml");
        var build = Context(BuildContextTypes.BuildContextTypeGuid, DpmCommandKind.Build, @"C:\repo\app", false, @"C:\repo\app\daml.yaml");
        var foreign = new FileContext(Guid.NewGuid(), BuildContextTypes.BuildContextTypeGuid, new object(), [@"C:\repo\CMakeLists.txt"], "Build");

        var action = (await provider.GetActionsAsync(@"C:\repo\app\daml.yaml", test, CancellationToken.None)).ShouldHaveSingleItem();
        await action.ExecuteAsync(null!, CancellationToken.None);

        var command = action.ShouldBeAssignableTo<IVsCommandItem>()!;
        (command.CommandGroup, command.CommandId).ShouldBe((BuildIds.CommandSet, BuildIds.TestCommandId));
        ((IVsCommandActionProvider)factory).GetSupportedVsCommands().Select(c => (c.Guid, c.ID)).ShouldBe([(BuildIds.CommandSet, (int)BuildIds.TestCommandId)]);
        action.DisplayName.ShouldBe("Test");
        ran.ShouldHaveSingleItem().Kind.ShouldBe(DpmCommandKind.Test);
        (await provider.GetActionsAsync(@"C:\repo\app\daml.yaml", build, CancellationToken.None)).ShouldBeEmpty();
        (await provider.GetActionsAsync(@"C:\repo\CMakeLists.txt", foreign, CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory, Trait("Category", "Editor")]
    [InlineData(@"C:\repo\app\daml.yaml", DpmCommandKind.Build, false)]
    [InlineData(@"C:\repo\app\daml.yaml", DpmCommandKind.Clean, false)]
    [InlineData(@"C:\repo\multi-package.yaml", DpmCommandKind.Build, true)]
    [InlineData(@"C:\repo\multi-package.yaml", DpmCommandKind.Clean, true)]
    public async Task Build_and_clean_contexts_are_run_by_the_folder_views_own_commands(string manifest, DpmCommandKind kind, bool all)
    {
        var ran = new List<DamlBuildAction>();
        var provider = new DamlFileContextProviderFactory((action, _) => { ran.Add(action); return Task.FromResult(true); }).CreateProvider(null!);
        var context = (await provider.GetContextsForFileAsync(manifest, CancellationToken.None))
            .Single(c => c.ContextType == DamlFileContextProviderFactory.ContextTypeFor(kind));

        var built = await context.Context.ShouldBeAssignableTo<IBuildFileContext>()!.ExecuteBuildAsync(null!, CancellationToken.None);

        built.ShouldBeTrue();
        var action = ran.ShouldHaveSingleItem();
        action.Kind.ShouldBe(kind);
        action.AllPackages.ShouldBe(all);
    }

    private static Task<bool> NoRun(DamlBuildAction action, CancellationToken cancellationToken) => Task.FromResult(false);

    private static FileContext Context(Guid type, DpmCommandKind kind, string folder, bool all, string manifest) =>
        new(BuildIds.FileContextProvider, type, new DamlBuildConfigurationContext(new DamlBuildAction(kind, folder, all), NoRun), [manifest], kind.ToString());

    [Fact, Trait("Category", "Editor")]
    public void The_error_list_gets_zero_based_entries_tagged_as_daml_build_and_follows_the_store()
    {
        var store = new BuildDiagnosticStore();
        using var source = new BuildErrorListSource(store);
        var sink = new RecordingSink();
        using var subscription = source.Subscribe(sink);

        store.Replace([new BuildDiagnostic(@"C:\repo\app\daml\Main.daml", 4, 8, 4, 19, DamlDiagnosticSeverity.Warning, "Unused import", "typecheck")]);
        store.ClearFile(@"C:\repo\app\daml\Main.daml");

        sink.Snapshots.Count.ShouldBe(3); // empty, one entry, empty again
        var snapshot = sink.Snapshots[1];
        snapshot.Count.ShouldBe(1);
        Value(snapshot, StandardTableKeyNames.Line).ShouldBe(4);
        Value(snapshot, StandardTableKeyNames.Column).ShouldBe(8);
        Value(snapshot, StandardTableKeyNames.DocumentName).ShouldBe(@"C:\repo\app\daml\Main.daml");
        Value(snapshot, StandardTableKeyNames.ErrorSeverity).ShouldBe(__VSERRORCATEGORY.EC_WARNING);
        Value(snapshot, StandardTableKeyNames.BuildTool).ShouldBe("Daml Build");
        Value(snapshot, StandardTableKeyNames.ErrorSource).ShouldBe(ErrorSource.Build);
        Value(snapshot, StandardTableKeyNames.Text).ShouldBe("Unused import");
        sink.Snapshots[2].Count.ShouldBe(0);
        source.SourceTypeIdentifier.ShouldBe(StandardTableDataSources.ErrorTableDataSource);
    }

    private static object? Value(ITableEntriesSnapshot snapshot, string key) =>
        snapshot.TryGetValue(0, key, out var value) ? value : null;

    private static async Task<DiscoveredParts> DiscoverAsync()
    {
        var discovery = PartDiscovery.Combine(
            new AttributedPartDiscoveryV1(Resolver.DefaultInstance),
            new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true));
        return await discovery.CreatePartsAsync(typeof(DamlPackage).Assembly);
    }

    private static List<ComposablePartDefinition> PartsExporting(DiscoveredParts parts, Type contract) =>
        parts.Parts.Where(p => p.ExportedTypes.Any(e => e.ContractName == ContractName(contract))).ToList();

    private static IEnumerable<ExportDefinition> Exports(DiscoveredParts parts, Type contract) =>
        parts.Parts.SelectMany(p => p.ExportingMembers.SelectMany(m => m.Value).Concat(p.ExportedTypes))
            .Where(e => e.ContractName == ContractName(contract));

    // MEF's contract names: the full type name, with generic arguments in parentheses.
    private static string ContractName(Type type) =>
        type.IsGenericType
            ? $"{type.Namespace}.{type.Name.Substring(0, type.Name.IndexOf('`'))}({string.Join(",", type.GetGenericArguments().Select(ContractName))})"
            : type.FullName!;

    // Metadata values may be single values or arrays (several attributes, or params arrays).
    private static IEnumerable<object?> Flatten(IEnumerable<object?> values)
    {
        foreach (var value in values)
        {
            if (value is IEnumerable items and not string)
            {
                foreach (var item in Flatten(items.Cast<object?>()))
                {
                    yield return item;
                }
            }
            else
            {
                yield return value;
            }
        }
    }

    private sealed class RecordingSink : ITableDataSink
    {
        public List<ITableEntriesSnapshot> Snapshots { get; } = new();

        public bool IsStable { get; set; } = true;

        public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots = false) => Snapshots.Add(newSnapshot);

        public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot) => Snapshots.Add(newSnapshot);

        public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries = false) => throw new NotSupportedException();

        public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries) => throw new NotSupportedException();

        public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries) => throw new NotSupportedException();

        public void RemoveAllEntries() => throw new NotSupportedException();

        public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot) => throw new NotSupportedException();

        public void RemoveAllSnapshots() => throw new NotSupportedException();

        public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories = false) => throw new NotSupportedException();

        public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory) => throw new NotSupportedException();

        public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory) => throw new NotSupportedException();

        public void FactorySnapshotChanged(ITableEntriesSnapshotFactory? factory) => throw new NotSupportedException();

        public void RemoveAllFactories() => throw new NotSupportedException();
    }
}
