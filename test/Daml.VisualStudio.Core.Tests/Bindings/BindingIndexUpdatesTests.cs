using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Core.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Bindings;

// The work items are owned by these tests and do not need a VS synchronization context.
#pragma warning disable VSTHRD003
public sealed class BindingIndexUpdatesTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingLogger _logger = new();
    private readonly List<(IndexChanges Changes, TaskCompletionSource<BindingIndex> Done, CancellationToken Token)> _applied = [];
    private readonly List<BindingIndex> _published = [];
    private readonly BindingIndexUpdates _updates;

    public BindingIndexUpdatesTests()
    {
        _updates = new BindingIndexUpdates(Apply, _time, Debounce, _logger);
        _updates.Published += (_, index) => { lock (_published) { _published.Add(index); } };
    }

    public void Dispose() => _updates.Dispose();

    private Task<BindingIndex> Apply(BindingIndex index, IndexChanges changes, CancellationToken token)
    {
        var done = new TaskCompletionSource<BindingIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => done.TrySetCanceled(token));
        lock (_applied)
        {
            _applied.Add((changes, done, token));
        }
        return done.Task;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        condition().ShouldBeTrue();
    }

    private int Applied { get { lock (_applied) { return _applied.Count; } } }

    private int Published { get { lock (_published) { return _published.Count; } } }

    private (IndexChanges Changes, TaskCompletionSource<BindingIndex> Done, CancellationToken Token) Last { get { lock (_applied) { return _applied[^1]; } } }

    private static BindingIndex Next(BindingIndex index) => index.WithoutProject("nothing").WithDocuments([]);

    [Fact, Trait("Category", "Unit")]
    public async Task A_change_is_read_once_things_have_been_quiet_for_the_debounce_and_published()
    {
        _updates.Request(IndexChanges.Document("app", @"C:\work\A.cs"));
        _time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50);
        Applied.ShouldBe(0);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => Applied == 1);
        Last.Changes.Documents.ShouldBe([("app", @"C:\work\A.cs")]);
        var result = Next(_updates.Current);
        Last.Done.SetResult(result);

        await Until(() => Published == 1);
        _updates.Current.ShouldBeSameAs(result);
    }

    // Each change restarts the wait; what came in the meantime is read together.
    [Fact, Trait("Category", "Unit")]
    public async Task A_burst_of_changes_is_read_in_one_go()
    {
        _updates.Request(IndexChanges.Document("app", @"C:\work\A.cs"));
        _time.Advance(TimeSpan.FromMilliseconds(600));
        _updates.Request(IndexChanges.Document("app", @"C:\work\B.cs"));
        _time.Advance(TimeSpan.FromMilliseconds(600));
        await Task.Delay(50);
        Applied.ShouldBe(0);

        _time.Advance(TimeSpan.FromMilliseconds(400));

        await Until(() => Applied == 1);
        Last.Changes.Documents.ShouldBe([("app", @"C:\work\A.cs"), ("app", @"C:\work\B.cs")], ignoreOrder: true);
    }

    // A read of an older solution is no use: it's dropped, and its changes are read with the new ones.
    [Fact, Trait("Category", "Unit")]
    public async Task A_change_during_a_read_cancels_it_and_folds_its_changes_into_the_next()
    {
        _updates.Request(IndexChanges.Document("app", @"C:\work\A.cs"));
        _time.Advance(Debounce);
        await Until(() => Applied == 1);
        var first = Last;

        _updates.Request(IndexChanges.Document("app", @"C:\work\B.cs"));
        first.Token.IsCancellationRequested.ShouldBeTrue();
        _time.Advance(Debounce);

        await Until(() => Applied == 2);
        Last.Changes.Documents.ShouldBe([("app", @"C:\work\A.cs"), ("app", @"C:\work\B.cs")], ignoreOrder: true);
        var result = Next(_updates.Current);
        Last.Done.SetResult(result);
        await Until(() => Published == 1);
        _published.ShouldHaveSingleItem().ShouldBeSameAs(result);
        first.Done.Task.IsCanceled.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_read_keeps_the_index_says_why_and_keeps_its_changes_for_the_next_one()
    {
        var before = _updates.Current;
        _updates.Request(IndexChanges.Document("app", @"C:\work\A.cs"));
        _time.Advance(Debounce);
        await Until(() => Applied == 1);
        Last.Done.SetException(new InvalidOperationException("the workspace is gone"));
        await Until(() => _logger.Entries.Any(e => e.Level == DamlLogLevel.Error));

        _updates.Request(IndexChanges.Document("app", @"C:\work\B.cs"));
        _time.Advance(Debounce);

        await Until(() => Applied == 2);
        _updates.Current.ShouldBeSameAs(before);
        _logger.Entries.ShouldContain(e => e.Exception != null && e.Exception.Message == "the workspace is gone");
        Last.Changes.Documents.Count.ShouldBe(2);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Disposing_cancels_the_read_and_ignores_later_changes()
    {
        _updates.Request(IndexChanges.Document("app", @"C:\work\A.cs"));
        _time.Advance(Debounce);
        await Until(() => Applied == 1);

        _updates.Dispose();
        _updates.Request(IndexChanges.Everything);
        _time.Advance(Debounce);
        await Task.Delay(50);

        Last.Token.IsCancellationRequested.ShouldBeTrue();
        Applied.ShouldBe(1);
    }
}

public class IndexChangesTests
{
    [Fact, Trait("Category", "Unit")]
    public void Reading_everything_again_covers_projects_and_documents_but_still_forgets_generations()
    {
        var changes = IndexChanges.Document("app", @"C:\A.cs")
            .Merge(IndexChanges.Project("lib"))
            .Merge(IndexChanges.Generation(@"C:\gen"))
            .Merge(IndexChanges.Everything)
            .Merge(IndexChanges.Document("app", @"C:\B.cs"));

        changes.All.ShouldBeTrue();
        (changes.Projects.Count, changes.RemovedProjects.Count, changes.Documents.Count).ShouldBe((0, 0, 0));
        changes.Generations.ShouldBe([@"C:\gen"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_project_read_again_covers_its_documents_and_the_last_of_adding_and_removing_wins()
    {
        var changes = IndexChanges.Document("app", @"C:\A.cs")
            .Merge(IndexChanges.Document("lib", @"C:\L.cs"))
            .Merge(IndexChanges.Project("app"))
            .Merge(IndexChanges.Document("app", @"C:\B.cs"))
            .Merge(IndexChanges.RemovedProject("lib"))
            .Merge(IndexChanges.RemovedProject("old"))
            .Merge(IndexChanges.Project("old"));

        changes.Projects.ShouldBe(["app", "old"], ignoreOrder: true);
        changes.RemovedProjects.ShouldBe(["lib"]);
        changes.Documents.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void The_same_document_twice_is_read_once_as_Windows_compares_paths() =>
        IndexChanges.Document("app", @"C:\A.cs").Merge(IndexChanges.Document("app", @"c:\a.CS")).Documents.Count.ShouldBe(1);

    [Fact, Trait("Category", "Unit")]
    public void No_change_is_empty() =>
        (IndexChanges.None.IsEmpty, IndexChanges.Generation(@"C:\gen").IsEmpty).ShouldBe((true, false));
}
