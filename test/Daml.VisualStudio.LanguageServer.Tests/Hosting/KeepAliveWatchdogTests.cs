using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Messages;
using Microsoft.Extensions.Time.Testing;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Hosting;

public sealed class KeepAliveWatchdogTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly DamlRpcChannel _channel = new();
    private readonly JsonRpc _rpc = new(Stream.Null);
    private readonly List<Ping> _pings = new();
    private readonly List<int> _unresponsive = new();

    public KeepAliveWatchdogTests() => _channel.Attach(_rpc, 1);

    public void Dispose() => _rpc.Dispose();

    private KeepAliveWatchdog? _watchdog;

    private KeepAliveWatchdog Watchdog(KeepAliveSettings? settings = null) => _watchdog = new(
        _channel,
        session => _unresponsive.Add(session),
        NullDamlLogger.Instance,
        _time,
        settings,
        (rpc, cancellationToken) =>
        {
            var ping = new Ping(rpc, cancellationToken);
            _pings.Add(ping);
#pragma warning disable VSTHRD003 // The test completes this task itself.
            return ping.Answer.Task;
#pragma warning restore VSTHRD003
        });

    private void Advance(int seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        Settle();
    }

    // The fake clock resumes the watchdog on the thread pool; wait until it is blocked again.
    private void Settle() =>
        SpinWait.SpinUntil(() => _watchdog?.IsSettled ?? true, TimeSpan.FromSeconds(10)).ShouldBeTrue("the watchdog didn't settle");

    [Fact, Trait("Category", "Unit")]
    public void A_late_stop_of_an_old_session_does_not_stop_the_new_watchdog()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        _channel.Attach(_rpc, 2);
        watchdog.Watch(2);
        watchdog.Stop(1);
        Advance(60);
        _pings.Count.ShouldBe(1);
        _pings[0].Rpc.ShouldBeSameAs(_rpc);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_first_ping_goes_out_a_minute_after_watching_starts_and_each_next_one_a_minute_after_the_answer()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);

        Advance(59);
        _pings.ShouldBeEmpty();
        Advance(1);
        _pings.Count.ShouldBe(1);
        _pings[0].Rpc.ShouldBeSameAs(_rpc);

        Advance(30);
        _pings[0].Answer.SetResult(true);
        Settle();
        Advance(59);
        _pings.Count.ShouldBe(1);
        Advance(1);
        _pings.Count.ShouldBe(2);
        _unresponsive.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void No_answer_within_two_minutes_reports_the_session_once_and_stops_pinging()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        Advance(60);

        Advance(119);
        _pings.Count.ShouldBe(1, "a ping is still in flight, so no second one");
        _unresponsive.ShouldBeEmpty();
        Advance(1);
        _unresponsive.ShouldBe([1]);

        Advance(600);
        _pings.Count.ShouldBe(1);
        _unresponsive.ShouldBe([1]);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_answer_just_before_the_deadline_keeps_the_server()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        Advance(60 + 119);

        _pings[0].Answer.SetResult(true);

        Settle();
        Advance(1);

        _unresponsive.ShouldBeEmpty();
        Advance(59);
        _pings.Count.ShouldBe(2);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_error_reply_still_proves_the_server_is_alive()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        Advance(60);

        _pings[0].Answer.SetException(new RemoteInvocationException("boom", 1, errorData: null));

        Settle();
        Advance(60);

        _pings.Count.ShouldBe(2);
        _unresponsive.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_server_without_keep_alive_or_a_lost_connection_ends_watching_without_a_report()
    {
        var methodNotFound = await MethodNotFoundAsync();
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        Advance(60);
        _pings[0].Answer.SetException(methodNotFound);
        Settle();
        Advance(600);
        _pings.Count.ShouldBe(1);

        watchdog.Watch(1);
        Advance(60);
        _pings[1].Answer.SetException(new ConnectionLostException());
        Settle();
        Advance(600);

        _pings.Count.ShouldBe(2);
        _unresponsive.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Stopping_drops_the_timer_and_a_late_answer_reports_nothing()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        Advance(60);
        var pending = _pings[0];

        watchdog.Stop();
        pending.Cancellation.IsCancellationRequested.ShouldBeTrue();
        Advance(600);
        pending.Answer.TrySetResult(true);
        Settle();
        Advance(600);

        _pings.Count.ShouldBe(1);
        _unresponsive.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_new_session_replaces_the_old_one_and_the_old_ping_cant_report_it()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        Advance(60);

        _channel.Attach(_rpc, 2);
        watchdog.Watch(2);
        Advance(59);
        _pings.Count.ShouldBe(1, "the new session's first ping is a minute after it started");
        Advance(1);
        _pings.Count.ShouldBe(2);

        Advance(119);
        _unresponsive.ShouldBeEmpty("the first session's ping ran out at 180 s, but it was no longer watched");
        Advance(1);
        _unresponsive.ShouldBe([2]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Nothing_is_sent_when_the_channel_belongs_to_another_session_or_watching_is_off()
    {
        using (var watchdog = Watchdog())
        {
            watchdog.Watch(7);
            Advance(600);
        }
        using (var disabled = Watchdog(new KeepAliveSettings { Enabled = false }))
        {
            disabled.Watch(1);
            Advance(600);
        }

        _pings.ShouldBeEmpty();
        _unresponsive.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void New_settings_apply_from_the_next_watch_and_switching_off_ends_watching()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        watchdog.Settings = new KeepAliveSettings { Interval = TimeSpan.FromSeconds(5) };
        Advance(5);
        _pings.ShouldBeEmpty("the running session keeps the settings it started with");

        watchdog.Watch(1);
        Advance(5);
        _pings.Count.ShouldBe(1);

        watchdog.Settings = new KeepAliveSettings { Enabled = false };
        watchdog.Watch(1);
        _pings[0].Cancellation.IsCancellationRequested.ShouldBeTrue();
        Advance(600);
        _pings.Count.ShouldBe(1);
        _unresponsive.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Intervals_come_from_the_settings()
    {
        using var watchdog = Watchdog(new KeepAliveSettings { Interval = TimeSpan.FromSeconds(5), Timeout = TimeSpan.FromSeconds(10) });
        watchdog.Watch(1);

        Advance(5);
        _pings.Count.ShouldBe(1);
        Advance(10);

        _unresponsive.ShouldBe([1]);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task The_default_ping_sends_daml_keepAlive_on_the_session_connection()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, new JsonMessageFormatter()));
        server.AddLocalRpcMethod("daml/keepAlive", new Func<object?>(() =>
        {
            received.TrySetResult(true);
            return null;
        }));
        server.StartListening();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, new JsonMessageFormatter()));
        client.StartListening();

        var ping = client.KeepAliveAsync(CancellationToken.None);
        (await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(ping);
        await ping;

        received.Task.IsCompleted.ShouldBeTrue();
    }

    // StreamJsonRpc only makes this exception itself, from a real reply.
    private static async Task<RemoteMethodNotFoundException> MethodNotFoundAsync()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, new JsonMessageFormatter()));
        server.StartListening();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, new JsonMessageFormatter()));
        client.StartListening();
        return await Should.ThrowAsync<RemoteMethodNotFoundException>(() => client.KeepAliveAsync(CancellationToken.None));
    }

    private sealed class Ping(JsonRpc rpc, CancellationToken cancellation)
    {
        public JsonRpc Rpc { get; } = rpc;

        public CancellationToken Cancellation { get; } = cancellation;

        public TaskCompletionSource<bool> Answer { get; } = new();
    }

    [Fact, Trait("Category", "Unit")]
    public void Watching_the_same_session_again_starts_over_and_the_old_ping_cant_report()
    {
        using var watchdog = Watchdog();
        watchdog.Watch(1);
        Advance(60);
        var first = _pings[0];

        watchdog.Watch(1);

        first.Cancellation.IsCancellationRequested.ShouldBeTrue("the ping in flight belongs to the run that ended");
        Advance(59);
        _pings.Count.ShouldBe(1);
        Advance(1);
        _pings.Count.ShouldBe(2, "the new run pings a minute after it started");
        Advance(119);
        _unresponsive.ShouldBeEmpty("the first ping ran out at 180 s, but its run is over");
        Advance(1);
        _unresponsive.ShouldBe([1], "the second ping's timeout, reported once");
    }

    [Fact, Trait("Category", "Unit")]
    public void Disposing_in_the_middle_of_a_ping_ends_it_without_a_report()
    {
        var watchdog = Watchdog();
        watchdog.Watch(1);
        Advance(60);
        var pending = _pings[0];

        watchdog.Dispose();

        pending.Cancellation.IsCancellationRequested.ShouldBeTrue();
        Settle();
        Advance(600);
        pending.Answer.TrySetResult(true);
        Settle();
        _pings.Count.ShouldBe(1);
        _unresponsive.ShouldBeEmpty();
    }
}
