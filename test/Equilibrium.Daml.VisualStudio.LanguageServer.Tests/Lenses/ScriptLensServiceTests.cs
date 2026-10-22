using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Server;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Equilibrium.Daml.VisualStudio.LanguageServer.Lenses;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Lenses;

public sealed class ScriptLensServiceTests : IDisposable
{
    private const string Main = "file:///C:/work/vs_daml/samples/single-package/daml/Main.daml";

    // damlc 3.6's answer for samples/single-package/daml/Main.daml.
    private static readonly JToken Answer = JToken.Parse("""
        [{"command":{"arguments":["Script: setup","daml://compiler?file=M%3A%2FMoje%2FProjekty%2Fvs_daml%2Fsamples%2Fsingle-package%2Fdaml%2FMain.daml&top-level-decl=setup"],
          "command":"daml.showResource","title":"Script results"},
          "range":{"end":{"character":5,"line":21},"start":{"character":0,"line":21}}}]
        """);

    private readonly FakeTimeProvider _time = new();
    private readonly DamlRpcChannel _channel = new();
    private readonly RecordingLogger _logger = new();
    private readonly List<Request> _requests = [];
    private readonly List<ScriptLensesChangedEventArgs> _changes = [];
    private readonly ScriptLensService _service;
    private readonly JsonRpc _rpc = new(Stream.Null);

    public ScriptLensServiceTests()
    {
        _service = new ScriptLensService(_channel, _logger, _time, new ScriptLensSettings(), (rpc, uri, token) =>
        {
            var request = new Request(uri, token);
            lock (_requests)
            {
                _requests.Add(request);
            }
            token.Register(() => request.Reply.TrySetCanceled(token));
            // The fake server's answer is completed by the test, on no particular context.
#pragma warning disable VSTHRD003
            return request.Reply.Task;
#pragma warning restore VSTHRD003
        });
        _service.LensesChanged += (_, e) =>
        {
            lock (_changes)
            {
                _changes.Add(e);
            }
        };
        _channel.Attach(_rpc, sessionId: 1);
    }

    public void Dispose()
    {
        _service.Dispose();
        _rpc.Dispose();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Lenses_are_asked_for_half_a_second_after_the_document_opens()
    {
        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(499));
        _requests.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        var request = await NextRequestAsync();
        request.Uri.ShouldBe(Main);
        request.Reply.SetResult(Answer);
        await SettledAsync(() => _changes.Count == 1);

        var lens = _service.GetLenses(Main).ShouldHaveSingleItem();
        lens.Title.ShouldBe("Script: setup");
        lens.Range.ShouldBe(new LensRange(21, 0, 21, 5));
        lens.Resource.ShouldBe(VirtualResourceUri.For(@"C:\work\vs_daml\samples\single-package\daml\Main.daml", "setup"));
        _changes[0].DocumentUri.ShouldBe(Main);
    }

    // damlc publishes diagnostics for daml.yaml too; asked for its lenses, it parses the file as a
    // Daml module and reports "parse error on input '3.6'" against it.
    [Theory, Trait("Category", "Unit")]
    [InlineData("file:///C:/work/vs_daml/samples/single-package/daml.yaml")]
    [InlineData("file:///C:/work/vs_daml/samples/multi-package.yaml")]
    [InlineData("file:///C:/work/vs_daml/samples/single-package/.daml/dist/single-package-0.1.0.dar")]
    public async Task Only_Daml_sources_are_asked_for_lenses(string uri)
    {
        _service.OnDocumentActivity(uri);
        _service.OnDocumentActivity(Main.ToUpperInvariant().Replace(".DAML", ".Daml"));
        _time.Advance(TimeSpan.FromMilliseconds(500));

        await NextRequestAsync();
        await Task.Delay(50);
        lock (_requests)
        {
            _requests.ShouldHaveSingleItem().Uri.ShouldEndWith(".Daml");
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Activity_in_quick_succession_makes_one_request()
    {
        for (var i = 0; i < 5; i++)
        {
            _service.OnDocumentActivity(Main);
            _time.Advance(TimeSpan.FromMilliseconds(200));
        }
        _time.Advance(TimeSpan.FromMilliseconds(500));

        await NextRequestAsync();
        await Task.Delay(50);
        _requests.Count.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_newer_request_cancels_the_one_in_flight_and_only_its_answer_counts()
    {
        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var first = await NextRequestAsync();

        _service.OnDocumentActivity(Main);
        first.Token.IsCancellationRequested.ShouldBeTrue();
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var second = await NextRequestAsync(1);
        second.Reply.SetResult(Answer);

        await SettledAsync(() => _changes.Count == 1);
        _service.GetLenses(Main).Count.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_request_leaves_the_document_without_lenses_and_says_so()
    {
        await LensesArrivedAsync();

        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        (await NextRequestAsync(1)).Reply.SetException(new RemoteInvocationException("boom", -32603, errorData: null));

        await SettledAsync(() => _changes.Count == 2);
        _service.GetLenses(Main).ShouldBeEmpty();
        _changes[1].Lenses.ShouldBeEmpty();
        _logger.Entries.ShouldContain(e => e.Message.Contains("Couldn't list the scripts") && e.Message.Contains("boom"));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_request_that_takes_too_long_is_given_up()
    {
        await LensesArrivedAsync();

        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var slow = await NextRequestAsync(1);
        _time.Advance(TimeSpan.FromSeconds(10));

        await SettledAsync(() => _changes.Count == 2);
        slow.Token.IsCancellationRequested.ShouldBeTrue();
        _service.GetLenses(Main).ShouldBeEmpty();
        _logger.Entries.ShouldContain(e => e.Message.Contains("within 10 s"));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_next_change_asks_again_after_a_failure()
    {
        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        (await NextRequestAsync()).Reply.SetException(new ConnectionLostException());

        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        (await NextRequestAsync(1)).Reply.SetResult(Answer);

        await SettledAsync(() => _service.GetLenses(Main).Count == 1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_answer_from_a_session_that_has_ended_is_dropped()
    {
        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var request = await NextRequestAsync();
        using var next = new JsonRpc(Stream.Null);

        _channel.Attach(next, sessionId: 2);
        request.Reply.TrySetResult(Answer);

        await Task.Delay(50);
        _service.GetLenses(Main).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_new_session_clears_the_lenses_of_the_old_one()
    {
        await LensesArrivedAsync();
        using var next = new JsonRpc(Stream.Null);

        _channel.Attach(next, sessionId: 2);

        _service.GetLenses(Main).ShouldBeEmpty();
        _changes.Last().DocumentUri.ShouldBe(Main);
        _changes.Last().Lenses.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_a_server_nothing_is_asked()
    {
        _channel.Detach(1);

        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromSeconds(1));

        await Task.Delay(50);
        _requests.ShouldBeEmpty();
        _changes.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_same_lenses_again_raise_nothing()
    {
        await LensesArrivedAsync();

        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        (await NextRequestAsync(1)).Reply.SetResult(Answer.DeepClone());

        await Task.Delay(50);
        _changes.Count.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task After_dispose_pending_requests_end_and_nothing_is_raised()
    {
        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var request = await NextRequestAsync();

        _service.Dispose();

        request.Token.IsCancellationRequested.ShouldBeTrue();
        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        _requests.Count.ShouldBe(1);
        _changes.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Uris_that_differ_only_in_escaping_or_case_are_one_document()
    {
        await LensesArrivedAsync();

        _service.GetLenses("file:///m%3A/moje/projekty/vs_daml/samples/single-package/daml/main.daml").Count.ShouldBe(1);
    }

    private async Task LensesArrivedAsync()
    {
        _service.OnDocumentActivity(Main);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        (await NextRequestAsync()).Reply.SetResult(Answer);
        await SettledAsync(() => _changes.Count == 1);
    }

    private async Task<Request> NextRequestAsync(int index = 0)
    {
        await SettledAsync(() => _requests.Count > index);
        lock (_requests)
        {
            return _requests[index];
        }
    }

    private static async Task SettledAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the service never got there");
            await Task.Delay(10);
        }
    }

    private sealed class Request(string uri, CancellationToken token)
    {
        public string Uri { get; } = uri;

        public CancellationToken Token { get; } = token;

        public TaskCompletionSource<JToken?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
