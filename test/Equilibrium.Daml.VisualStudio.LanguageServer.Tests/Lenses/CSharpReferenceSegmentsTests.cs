using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.LanguageServer.Lenses;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Lenses;

// "C#: N references" above templates, interfaces and choices: counted only while on screen, two at
// a time, kept until the C# or the index changes, and never a misleading 0.
public sealed class CSharpReferenceSegmentsTests : IDisposable
{
    private const string File = @"C:\sln\Contracts\daml\Main.daml";
    private static readonly string Uri = new Uri(File).AbsoluteUri;

    // Four templates with a choice each, one declaration every five lines.
    private static readonly string Text = "module Main where\n\n" + string.Concat(Enumerable.Range(0, 4).Select(i =>
        $"template T{i}\n  with p : Party\n  where\n    signatory p\n    choice C{i} : ()\n      controller p\n      do pure ()\n\n"));

    private readonly FakeSource _source = new();
    private readonly CSharpReferenceSegments _segments;
    private readonly List<string> _changed = [];

    public CSharpReferenceSegmentsTests()
    {
        _segments = new CSharpReferenceSegments(_source);
        _segments.SegmentsChanged += (_, uri) =>
        {
            lock (_changed)
            {
                _changed.Add(uri);
            }
        };
    }

    public void Dispose() => _segments.Dispose();

    private string[] Texts() => [.. _segments.SegmentsFor(Uri).Select(s => s.Text)];

    private void Show(int first, int last, int version = 1) => _segments.Shown(Uri, first, last, version, () => Text);

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(5);
        }
    }

    // Counts start and end on the thread pool; waits for what a test expects, a few seconds at most.
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void A_file_whose_package_generates_no_CSharp_has_no_segments()
    {
        _source.Link = CSharpLinkState.None;

        Show(0, 100);

        _segments.SegmentsFor(Uri).ShouldBeEmpty();
        _source.Asked.ShouldBeEmpty();
    }

    // The bindings are from other sources than these: any count would be about code that changed since.
    [Fact, Trait("Category", "Unit")]
    public void Out_of_date_bindings_say_so_instead_of_a_count()
    {
        _source.Link = CSharpLinkState.Stale;

        Show(0, 100);

        Texts().ShouldAllBe(t => t == "C#: out of date (build to update)");
        Texts().Length.ShouldBe(8);
        _source.Asked.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Only_declarations_on_screen_are_counted_and_the_rest_wait()
    {
        Show(0, 9);
        await UntilAsync(() => _source.Asked.Count == 2);

        _source.Asked.ShouldBe(["T0", "T0.C0"], ignoreOrder: true);
        _source.Answer("T0", 2);
        _source.Answer("T0.C0", 0);
        await UntilAsync(() => Texts().Contains("C#: 0 references") && Texts().Contains("C#: 2 references"));

        var segments = _segments.SegmentsFor(Uri);
        segments.Select(s => (s.Line, s.Text)).Take(3).ShouldBe([(2, "C#: 2 references"), (6, "C#: 0 references"), (10, "C#: …")]);
        segments[0].AutomationName.ShouldBe("Show the C# references of template T0");
        segments[1].AutomationName.ShouldBe("Show the C# references of choice T0.C0");
        segments[0].Target.ShouldBe(new CSharpReferenceLens(File, "Main", new DamlLensDeclaration(DamlSymbolKind.Template, "T0", null, 2)));
        lock (_changed)
        {
            _changed.ShouldContain(Uri);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task No_more_than_two_counts_run_at_once()
    {
        Show(0, 100);
        await UntilAsync(() => _source.Running == 2);
        await SettleAsync();

        _source.Running.ShouldBe(2);
        _source.Answer(_source.Asked[0], 1);
        await UntilAsync(() => _source.Asked.Count == 3 && _source.Running == 2);
        await SettleAsync();
        _source.Running.ShouldBe(2);
        _source.Asked.Count.ShouldBe(3);
    }

    // Scrolled away before the answer came: that count is no longer worth the work.
    [Fact, Trait("Category", "Unit")]
    public async Task Scrolling_away_cancels_counts_no_longer_on_screen()
    {
        Show(0, 9);
        await UntilAsync(() => _source.Asked.Count == 2);

        Show(26, 40);
        await UntilAsync(() => _source.Asked.Count == 4);

        _source.Cancelled.ShouldBe(["T0", "T0.C0"], ignoreOrder: true);
        _source.Asked.ShouldBe(["T0", "T0.C0", "T3", "T3.C3"], ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_count_stands_until_the_CSharp_changes()
    {
        Show(0, 5);
        await UntilAsync(() => _source.Asked.Count == 1);
        _source.Answer("T0", 3);
        await UntilAsync(() => Texts()[0] == "C#: 3 references");

        Show(0, 5, version: 2);
        await SettleAsync();
        _source.Asked.ShouldBe(["T0"]);

        _source.RaiseChanged();
        await UntilAsync(() => _source.Asked.Count == 2);
        _source.Asked.ShouldBe(["T0", "T0"]);
        Texts()[0].ShouldBe("C#: 3 references", "the old count shows until the new one arrives");
    }

    // The C# workspace changes over and over while a solution loads; a count under way still ends and
    // shows, and the next one starts after it, so the row never stays "C#: …" for good.
    [Fact, Trait("Category", "Unit")]
    public async Task A_count_under_way_finishes_however_often_the_CSharp_changes()
    {
        Show(0, 5);
        await UntilAsync(() => _source.Asked.Count == 1);

        _source.RaiseChanged();
        _source.RaiseChanged();
        await SettleAsync();
        _source.Cancelled.ShouldBeEmpty();
        _source.Answer("T0", 2);
        await UntilAsync(() => Texts()[0] == "C#: 2 references" && _source.Asked.Count == 2);

        Texts()[0].ShouldBe("C#: 2 references");
        _source.Asked.ShouldBe(["T0", "T0"], "the changes since asked for a fresh count");
    }

    // Something went wrong in Roslyn: the row says so, the reason is reported, and the next change tries again.
    [Fact, Trait("Category", "Unit")]
    public async Task A_count_that_fails_says_so_and_is_tried_again_after_a_change()
    {
        var failures = new List<Exception>();
        _segments.CountFailed += (_, e) =>
        {
            lock (failures)
            {
                failures.Add(e.Exception);
            }
        };
        Show(0, 5);
        await UntilAsync(() => _source.Asked.Count == 1);

        _source.Fail("T0", new InvalidOperationException("no compilation"));
        // The row changes under the provider's lock, the event comes just after it.
        await UntilAsync(() =>
        {
            lock (failures)
            {
                return Texts()[0] == "C#: couldn't count (see Output)" && failures.Count == 1;
            }
        });

        Texts()[0].ShouldBe("C#: couldn't count (see Output)");
        lock (failures)
        {
            failures.ShouldHaveSingleItem().Message.ShouldBe("no compilation");
        }
        _source.RaiseChanged();
        await UntilAsync(() => _source.Asked.Count == 2);
        _source.Asked.Count.ShouldBe(2);
    }

    // A save makes the sources newer than the bindings; the source says so with a change, and the rows follow.
    [Fact, Trait("Category", "Unit")]
    public async Task Rows_follow_the_link_when_the_source_reports_a_change()
    {
        Show(0, 100);
        await UntilAsync(() => _source.Asked.Count == 2);

        _source.Link = CSharpLinkState.Stale;
        _source.RaiseChanged();
        await UntilAsync(() => Texts().All(t => t == "C#: out of date (build to update)"));

        Texts().ShouldAllBe(t => t == "C#: out of date (build to update)");
        lock (_changed)
        {
            _changed.Count(u => u == Uri).ShouldBeGreaterThanOrEqualTo(2);
        }
    }

    // A declaration added since the last build has no C# yet; its row would only mislead.
    [Fact, Trait("Category", "Unit")]
    public async Task A_declaration_without_generated_CSharp_has_no_segment()
    {
        Show(0, 5);
        await UntilAsync(() => _source.Asked.Count == 1);

        _source.Answer("T0", null);
        await UntilAsync(() => !_segments.SegmentsFor(Uri).Any(s => s.Line == 2));

        _segments.SegmentsFor(Uri).ShouldNotContain(s => s.Line == 2);
    }

    // Each count waits for the test, or is cancelled.
    private sealed class FakeSource : ICSharpReferenceSource
    {
        private readonly object _gate = new();
        private readonly List<(string Name, TaskCompletionSource<IReadOnlyList<CSharpReference>?> Result)> _runs = [];
        private readonly List<string> _cancelled = [];
        private int _running;

        public event EventHandler? Changed;

        public CSharpLinkState Link { get; set; } = CSharpLinkState.Current;

        public List<string> Asked
        {
            get
            {
                lock (_gate)
                {
                    return [.. _runs.Select(r => r.Name)];
                }
            }
        }

        public List<string> Cancelled
        {
            get
            {
                lock (_gate)
                {
                    return [.. _cancelled];
                }
            }
        }

        public int Running => Volatile.Read(ref _running);

        public CSharpLinkState LinkOf(string damlFile) => Link;

        public async Task<IReadOnlyList<CSharpReference>?> FindAsync(string damlFile, string damlModule, DamlLensDeclaration declaration, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<IReadOnlyList<CSharpReference>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _runs.Add((declaration.Name, result));
            }
            Interlocked.Increment(ref _running);
            try
            {
                using (cancellationToken.Register(() =>
                {
                    lock (_gate)
                    {
                        _cancelled.Add(declaration.Name);
                    }
                    result.TrySetCanceled();
                }))
                {
                    return await result.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public void Answer(string name, int? count)
        {
            lock (_gate)
            {
                var references = count is { } n
                    ? Enumerable.Range(0, n).Select(i => new CSharpReference("App", @"C:\sln\App\Program.cs", i + 1, 1, i * 10, 2, "x")).ToList()
                    : null;
                _runs.Last(r => r.Name == name).Result.TrySetResult(references);
            }
        }

        public void Fail(string name, Exception exception)
        {
            lock (_gate)
            {
                _runs.Last(r => r.Name == name).Result.TrySetException(exception);
            }
        }

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
