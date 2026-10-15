using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Messages;
using Daml.VisualStudio.LanguageServer.VirtualResources;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.VirtualResources;

public sealed class VirtualResourceManagerTests : IDisposable
{
    private const string Setup = "daml://compiler?file=C%3A%2Fwork%2Fdaml%2FMain.daml&top-level-decl=setup";
    private const string Other = "daml://compiler?file=C%3A%2Fwork%2Fdaml%2FMain.daml&top-level-decl=other";
    private const string Results = """<div class="table">results</div>""";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly DamlRpcChannel _channel = new();
    private readonly RecordingLogger _logger = new();
    private readonly FakeWindows _windows = new();
    private readonly List<(int Session, string Method, string Uri)> _sent = [];
    private readonly JsonRpc _session1 = new(Stream.Null);
    private readonly VirtualResourceManager _manager;

    public VirtualResourceManagerTests()
    {
        _manager = new VirtualResourceManager(_channel, _windows, _logger, _time, (rpc, method, uri) =>
        {
            lock (_sent)
            {
                _sent.Add((_channel.TryGet(out _, out var id) ? id : 0, method, uri));
            }
            return Task.CompletedTask;
        });
        _channel.Attach(_session1, sessionId: 1);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _session1.Dispose();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Showing_a_resource_opens_its_window_then_asks_the_server_for_it()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);

        _windows.Calls.ShouldBe(["show Script: setup " + Setup]);
        _sent.ShouldBe([(1, "textDocument/didOpen", Setup)]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Showing_it_again_brings_the_window_up_without_opening_it_twice()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);

        await _manager.ShowAsync("Script: setup", "daml://compiler?file=c%3A%2FWORK%2Fdaml%2Fmain.daml&top-level-decl=setup", CancellationToken.None);

        _windows.Calls.Count(c => c.StartsWith("show", StringComparison.Ordinal)).ShouldBe(2);
        _sent.Count.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task New_contents_go_into_the_window_without_bringing_it_up()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);

        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        await _manager.IdleAsync();

        _windows.Calls.Last().ShouldBe("update " + Setup + " " + Results);
        _windows.Calls.Count(c => c.StartsWith("show", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_same_contents_again_are_not_shown_again()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);

        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        await _manager.IdleAsync();

        _windows.Calls.Count(c => c.StartsWith("update", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Notifications_for_a_resource_without_a_window_are_dropped()
    {
        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        _manager.Noted(new VirtualResourceNoteParams(Setup, "note"));
        _manager.Progressed(new VirtualResourceProgressParams(Setup, 100, 0));
        await _manager.IdleAsync();

        _windows.Calls.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Notes_are_added_to_the_window()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);

        _manager.Noted(new VirtualResourceNoteParams(Setup, "Script failed"));
        await _manager.IdleAsync();

        _windows.Calls.Last().ShouldBe("note " + Setup + " Script failed");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Progress_of_a_run_started_before_the_latest_results_is_ignored()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);
        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        await _manager.IdleAsync();

        _manager.Progressed(new VirtualResourceProgressParams(Setup, 250, StartedAt: _time.GetUtcNow().ToUnixTimeMilliseconds() - 1));
        await _manager.IdleAsync();

        _windows.Calls.Last().ShouldBe("update " + Setup + " " + Results);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Progress_of_a_newer_run_says_how_long_it_has_been_going()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);
        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        await _manager.IdleAsync();
        _time.Advance(TimeSpan.FromSeconds(5));

        _manager.Progressed(new VirtualResourceProgressParams(Setup, 1500, StartedAt: _time.GetUtcNow().ToUnixTimeMilliseconds() - 1500));
        await _manager.IdleAsync();

        _windows.Calls.Last().ShouldContain("Virtual resource has been running for 1500 ms");
        _windows.Calls.Last().ShouldStartWith("update " + Setup);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Closing_the_window_closes_the_resource_on_the_server()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);

        _windows.Close(Setup);
        await _manager.IdleAsync();

        _sent.Last().ShouldBe((1, "textDocument/didClose", Setup));
        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        await _manager.IdleAsync();
        _windows.Calls.ShouldNotContain(c => c.StartsWith("update", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_resource_opened_before_a_restart_is_opened_again_once_the_new_session_is_ready()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);
        await _manager.ShowAsync("Script: other", Other, CancellationToken.None);
        using var session2 = new JsonRpc(Stream.Null);

        _channel.Attach(session2, sessionId: 2);
        _sent.Count.ShouldBe(2, "not before the new session is ready");
        _manager.OnSessionReady(2);
        _manager.OnSessionReady(2);
        await _manager.IdleAsync();

        _sent.Where(s => s.Session == 2).Select(s => (s.Method, s.Uri)).ShouldBe(
            [("textDocument/didOpen", Setup), ("textDocument/didOpen", Other)], ignoreOrder: true);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Contents_that_arrive_from_a_session_the_resource_isnt_open_in_are_dropped()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);
        using var session2 = new JsonRpc(Stream.Null);
        _channel.Attach(session2, sessionId: 2);

        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        await _manager.IdleAsync();
        _windows.Calls.ShouldNotContain(c => c.StartsWith("update", StringComparison.Ordinal));

        _manager.OnSessionReady(2);
        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        await _manager.IdleAsync();
        _windows.Calls.Last().ShouldStartWith("update " + Setup);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Closing_after_the_session_ended_tells_no_server()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);
        _channel.Detach(1);

        _windows.Close(Setup);
        await _manager.IdleAsync();

        _sent.ShouldNotContain(s => s.Method == "textDocument/didClose");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_a_server_the_window_opens_and_the_resource_is_asked_for_when_one_is_ready()
    {
        _channel.Detach(1);

        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);
        _sent.ShouldBeEmpty();
        _windows.Calls.ShouldBe(["show Script: setup " + Setup]);

        using var session2 = new JsonRpc(Stream.Null);
        _channel.Attach(session2, sessionId: 2);
        _manager.OnSessionReady(2);
        await _manager.IdleAsync();
        _sent.ShouldBe([(2, "textDocument/didOpen", Setup)]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_window_that_fails_is_logged_and_the_rest_carry_on()
    {
        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);
        await _manager.ShowAsync("Script: other", Other, CancellationToken.None);
        _windows.FailUpdatesOf = Setup;

        _manager.Changed(new VirtualResourceChangedParams(Setup, Results));
        _manager.Changed(new VirtualResourceChangedParams(Other, Results));
        await _manager.IdleAsync();

        _logger.Entries.ShouldContain(e => e.Message.Contains("Couldn't show"));
        _windows.Calls.Last().ShouldBe("update " + Other + " " + Results);
    }

    // A window that never came up (it failed, or its folder closed meanwhile) can't be closed, so
    // nothing would ever forget its resource, and the next show wouldn't ask the server again.
    [Fact, Trait("Category", "Unit")]
    public async Task A_show_whose_window_does_not_open_leaves_nothing_behind()
    {
        _windows.FailShowsOf = Setup;
        await Should.ThrowAsync<OperationCanceledException>(() => _manager.ShowAsync("Script: setup", Setup, CancellationToken.None));
        _windows.FailShowsOf = null;

        await _manager.ShowAsync("Script: setup", Setup, CancellationToken.None);

        _sent.ShouldBe([(1, "textDocument/didOpen", Setup)]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Something_that_isnt_a_virtual_resource_is_refused()
    {
        await Should.ThrowAsync<ArgumentException>(() => _manager.ShowAsync("x", "file:///C:/work/Main.daml", CancellationToken.None));

        _windows.Calls.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    private sealed class FakeWindows : IVirtualResourceWindows
    {
        public List<string> Calls { get; } = [];

        public string? FailUpdatesOf { get; set; }

        public string? FailShowsOf { get; set; }

        public event EventHandler<string>? Closed;

        public Task ShowAsync(string resource, string title, CancellationToken cancellationToken)
        {
            if (resource == FailShowsOf)
            {
                return Task.FromException(new OperationCanceledException("the folder closed"));
            }
            Record($"show {title} {resource}");
            return Task.CompletedTask;
        }

        public Task UpdateAsync(string resource, string html)
        {
            if (resource == FailUpdatesOf)
            {
                throw new InvalidOperationException("window broke");
            }
            Record($"update {resource} {html}");
            return Task.CompletedTask;
        }

        public Task AddNoteAsync(string resource, string text)
        {
            Record($"note {resource} {text}");
            return Task.CompletedTask;
        }

        public void Close(string resource) => Closed?.Invoke(this, resource);

        private void Record(string call)
        {
            lock (Calls)
            {
                Calls.Add(call);
            }
        }
    }
}
