using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;
using static Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Protocol.LspFramingTests;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Protocol;

// The array overloads are the ones the adapter streams implement (netstandard2.0 has no Memory
// overloads to override), so the tests call those on purpose.
#pragma warning disable CA1835

public class LspStreamAdapterTests
{
    private static readonly LspAdapterSettings Settings = new() { MaxFrameBytes = 4096, MaxHeaderBytes = 256 };

    // Shape of what VS 18.11 sends, trimmed to the parts the rules touch.
    private const string VsInitialize = """
        {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"processId":1,"capabilities":{"textDocument":{
          "completion":{"completionItemKind":{"valueSet":[0,1,2,25,26,118115,118126]},"completionItem":{"snippetSupport":true}},
          "publishDiagnostics":{"tagSupport":{"valueSet":[1,2,2147483645,2147483646]}}}}}}
        """;

    private const string Initialized = """{"jsonrpc":"2.0","method":"initialized","params":{}}""";

    private static JObject Json(byte[] body) => JObject.Parse(Encoding.UTF8.GetString(body));

    private static SanitizedMessage Sanitize(string json) => ClientMessageSanitizer.Sanitize(Encoding.UTF8.GetBytes(json));

    [Fact, Trait("Category", "Unit")]
    public void Initialize_keeps_only_completion_kinds_and_diagnostic_tags_the_spec_defines()
    {
        var result = Sanitize(VsInitialize);

        result.Action.ShouldBe(SanitizeAction.Forward);
        var capabilities = Json(result.Body)["params"]!["capabilities"]!["textDocument"]!;
        capabilities["completion"]!["completionItemKind"]!["valueSet"]!.Values<int>().ShouldBe([1, 2, 25]);
        capabilities["publishDiagnostics"]!["tagSupport"]!["valueSet"]!.Values<int>().ShouldBe([1, 2]);
        capabilities["completion"]!["completionItem"]!["snippetSupport"]!.Value<bool>().ShouldBeTrue();
        result.InitializeRequestId.ShouldNotBeNull().Value<int>().ShouldBe(0);
    }

    // Everything damlc decodes as a closed enum: a value outside the protocol's range anywhere
    // in initialize would end the session before it began.
    [Fact, Trait("Category", "Unit")]
    public void Initialize_keeps_only_the_symbol_kinds_item_tags_and_insert_modes_the_spec_defines()
    {
        var result = Sanitize("""
            {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"processId":1,"capabilities":{
              "workspace":{"symbol":{"symbolKind":{"valueSet":[1,26,27,118000]}}},
              "textDocument":{
                "documentSymbol":{"symbolKind":{"valueSet":[0,5,26,99]}},
                "completion":{"completionItem":{"tagSupport":{"valueSet":[1,2]},"insertTextModeSupport":{"valueSet":[1,2,3]}}}}}}}
            """);

        var capabilities = Json(result.Body)["params"]!["capabilities"]!;
        capabilities["workspace"]!["symbol"]!["symbolKind"]!["valueSet"]!.Values<int>().ShouldBe([1, 26]);
        capabilities["textDocument"]!["documentSymbol"]!["symbolKind"]!["valueSet"]!.Values<int>().ShouldBe([5, 26]);
        capabilities["textDocument"]!["completion"]!["completionItem"]!["tagSupport"]!["valueSet"]!.Values<int>().ShouldBe([1]);
        capabilities["textDocument"]!["completion"]!["completionItem"]!["insertTextModeSupport"]!["valueSet"]!.Values<int>().ShouldBe([1, 2]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Diagnostics_sent_back_by_the_client_lose_private_tags()
    {
        var result = Sanitize("""
            {"jsonrpc":"2.0","id":7,"method":"textDocument/codeAction","params":{"context":{"diagnostics":[
              {"message":"a","tags":[1,2147483645]},{"message":"b","tags":[2147483646]},{"message":"c"}]}}}
            """);

        var diagnostics = Json(result.Body)["params"]!["context"]!["diagnostics"]!;
        diagnostics[0]!["tags"]!.Values<int>().ShouldBe([1]);
        diagnostics[1]!["tags"]!.Values<int>().ShouldBeEmpty();
        diagnostics[2]!["tags"].ShouldBeNull();
        result.InitializeRequestId.ShouldBeNull();
    }

    // The message is written out again after a change; a string that looks like a date must
    // come out exactly as it went in.
    [Fact, Trait("Category", "Unit")]
    public void A_rewritten_message_keeps_date_like_strings_as_they_were()
    {
        var result = Sanitize("""
            {"jsonrpc":"2.0","id":7,"method":"textDocument/codeAction","params":{"context":{"diagnostics":[
              {"message":"deprecated since 2026-10-13T10:00:00Z","tags":[1,2147483645]}]}}}
            """);

        Encoding.UTF8.GetString(result.Body).ShouldContain("\"deprecated since 2026-10-13T10:00:00Z\"");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_document_that_merely_mentions_tags_is_passed_on_as_the_same_bytes()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"contentChanges":[{"text":"data T = T with \"tags\" : [Int]"}]}}""");

        ClientMessageSanitizer.Sanitize(body).Body.ShouldBeSameAs(body);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_clients_own_NotificationReceived_never_reaches_the_server() =>
        Sanitize("""{"jsonrpc":"2.0","method":"NotificationReceived","params":[{"traceparent":"00-ab"}]}""").Action.ShouldBe(SanitizeAction.Drop);

    [Theory, Trait("Category", "Unit")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"textDocument/hover","params":{"position":{"line":1,"character":2}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"textDocument/codeAction","params":{"context":{"diagnostics":[{"tags":[1,2]}]}}}""")]
    [InlineData("not json \"tags\" at all")]
    public void Messages_needing_no_change_are_passed_on_as_the_very_same_bytes(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);

        var result = ClientMessageSanitizer.Sanitize(body);

        result.Action.ShouldBe(SanitizeAction.Forward);
        result.Body.ShouldBeSameAs(body);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Writes_from_the_client_are_reframed_sanitised_and_filtered_however_they_are_split(int chunk)
    {
        using var server = new MemoryStream();
        using var toServer = new ClientToServerStream(server, Settings, new InitializeTracker(), NullDamlLogger.Instance);
        var data = Concat(
            Frame(VsInitialize),
            Frame("""{"jsonrpc":"2.0","method":"NotificationReceived","params":[]}"""),
            Frame("""{"jsonrpc":"2.0","method":"initialized","params":{}}"""));

        for (var i = 0; i < data.Length; i += chunk)
        {
            await toServer.WriteAsync(data, i, Math.Min(chunk, data.Length - i));
        }

        var frames = await ReadAllFramesAsync(server.ToArray());
        frames.Select(f => (string)f["method"]!).ShouldBe(["initialize", "initialized"]);
        frames[0]["params"]!["capabilities"]!["textDocument"]!["completion"]!["completionItemKind"]!["valueSet"]!.Values<int>().ShouldBe([1, 2, 25]);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_synchronous_paths_behave_like_the_asynchronous_ones()
    {
        var tracker = new InitializeTracker();
        var ready = false;
        tracker.Initialized += (_, _) => ready = true;
        using var server = new MemoryStream();
        using var toServer = new ClientToServerStream(server, Settings, tracker, NullDamlLogger.Instance);
        var data = Concat(Frame(VsInitialize), Frame("""{"jsonrpc":"2.0","method":"NotificationReceived","params":[]}"""));
        toServer.Write(data, 0, 10);
        toServer.Write(data, 10, data.Length - 10);
        var reply = Frame("""{"jsonrpc":"2.0","id":0,"result":{}}""");
        using var fromServer = new ServerToClientStream(new MemoryStream(reply), Settings, tracker, NullDamlLogger.Instance);
        using var received = new MemoryStream();
        var buffer = new byte[16];
        int n;
        while ((n = fromServer.Read(buffer, 0, buffer.Length)) > 0)
        {
            received.Write(buffer, 0, n);
        }
        var initialized = Frame(Initialized);
        toServer.Write(initialized, 0, initialized.Length);
        toServer.Flush();

        var forwarded = new LspFrameReader(new MemoryStream(server.ToArray()), Settings);
        Json(forwarded.ReadFrame()!)["method"]!.ToString().ShouldBe("initialize");
        Json(forwarded.ReadFrame()!)["method"]!.ToString().ShouldBe("initialized");
        forwarded.ReadFrame().ShouldBeNull();
        received.ToArray().ShouldBe(reply);
        ready.ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_oversized_frame_from_the_client_is_refused()
    {
        using var toServer = new ClientToServerStream(new MemoryStream(), Settings, new InitializeTracker(), NullDamlLogger.Instance);
        var header = Encoding.ASCII.GetBytes("Content-Length: 100000\r\n\r\n");

        await Should.ThrowAsync<LspFramingException>(() => toServer.WriteAsync(header, 0, header.Length));
    }

    // Visual Studio counts a server as started once it has sent initialized; one that dies between
    // the reply and that notification is reported as failed to start and never restarted by it.
    [Theory, Trait("Category", "Unit")]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    public async Task Server_output_reaches_the_client_unchanged_and_the_handshake_completes_when_the_client_says_initialized(int chunk)
    {
        var tracker = new InitializeTracker();
        var ready = 0;
        tracker.Initialized += (_, _) => ready++;
        using var toServer = new ClientToServerStream(new MemoryStream(), Settings, tracker, NullDamlLogger.Instance);
        var init = Frame(VsInitialize);
        await toServer.WriteAsync(init, 0, init.Length);
        var serverOutput = Concat(
            Frame("""{"jsonrpc":"2.0","method":"window/logMessage","params":{"type":3,"message":"hi"}}"""),
            Frame("""{"jsonrpc":"2.0","id":0,"result":{"capabilities":{}}}"""),
            Frame("""{"jsonrpc":"2.0","id":0,"result":{"capabilities":{}}}"""));
        using var fromServer = new ServerToClientStream(new TrickleStream(serverOutput, chunk), Settings, tracker, NullDamlLogger.Instance);

        var received = await ReadToEndAsync(fromServer, 3);
        var readyAfterReply = ready;
        var initialized = Frame(Initialized);
        await toServer.WriteAsync(initialized, 0, initialized.Length);
        await toServer.WriteAsync(initialized, 0, initialized.Length);

        received.ShouldBe(serverOutput);
        readyAfterReply.ShouldBe(0, "the reply alone doesn't finish the handshake");
        ready.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_initialized_seen_before_the_reply_completes_the_handshake_when_the_reply_comes()
    {
        var tracker = new InitializeTracker();
        var ready = 0;
        tracker.Initialized += (_, _) => ready++;
        using var toServer = new ClientToServerStream(new MemoryStream(), Settings, tracker, NullDamlLogger.Instance);
        var messages = Concat(Frame(VsInitialize), Frame(Initialized));
        await toServer.WriteAsync(messages, 0, messages.Length);
        var readyBeforeReply = ready;

        using var fromServer = new ServerToClientStream(new MemoryStream(Frame("""{"jsonrpc":"2.0","id":0,"result":{}}""")), Settings, tracker, NullDamlLogger.Instance);
        await ReadToEndAsync(fromServer, 64);

        readyBeforeReply.ShouldBe(0);
        ready.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_initialized_that_does_not_reach_the_server_leaves_the_handshake_open()
    {
        var tracker = new InitializeTracker();
        var ready = 0;
        tracker.Initialized += (_, _) => ready++;
        var server = new ServerPipe();
        using var toServer = new ClientToServerStream(server, Settings, tracker, NullDamlLogger.Instance);
        var init = Frame(VsInitialize);
        await toServer.WriteAsync(init, 0, init.Length);
        using var fromServer = new ServerToClientStream(new MemoryStream(Frame("""{"jsonrpc":"2.0","id":0,"result":{}}""")), Settings, tracker, NullDamlLogger.Instance);
        await ReadToEndAsync(fromServer, 64);

        server.Gone = true;
        var initialized = Frame(Initialized);
        await Should.ThrowAsync<IOException>(() => toServer.WriteAsync(initialized, 0, initialized.Length));

        ready.ShouldBe(0);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_error_reply_to_initialize_is_reported_as_a_failure()
    {
        var tracker = new InitializeTracker();
        tracker.Expect(new JValue(5));
        string? failure = null;
        tracker.Failed += (_, e) => failure = e.Message;
        var serverOutput = Frame("""{"jsonrpc":"2.0","id":5,"error":{"code":-32603,"message":"SDK 3.9.9 not installed"}}""");
        using var fromServer = new ServerToClientStream(new MemoryStream(serverOutput), Settings, tracker, NullDamlLogger.Instance);

        await ReadToEndAsync(fromServer, 64);

        failure.ShouldBe("SDK 3.9.9 not installed");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Broken_server_framing_ends_the_connection_with_an_IO_error_and_tells_the_owner()
    {
        Exception? reported = null;
        using var fromServer = new ServerToClientStream(new MemoryStream(Encoding.ASCII.GetBytes("garbage without headers\r\n\r\n")), Settings, new InitializeTracker(), NullDamlLogger.Instance, onFailure: ex => reported = ex);

        await Should.ThrowAsync<IOException>(() => ReadToEndAsync(fromServer, 64));

        reported.ShouldBeOfType<LspFramingException>();
    }

    private static async Task<List<JObject>> ReadAllFramesAsync(byte[] data)
    {
        var reader = new LspFrameReader(new MemoryStream(data), Settings);
        var frames = new List<JObject>();
        while (await reader.ReadFrameAsync(CancellationToken.None) is { } body)
        {
            frames.Add(Json(body));
        }
        return frames;
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream, int bufferSize)
    {
        using var all = new MemoryStream();
        var buffer = new byte[bufferSize];
        int n;
        while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await all.WriteAsync(buffer, 0, n);
        }
        return all.ToArray();
    }

    // The pipe to a server process: writes fail once the process is gone.
    private sealed class ServerPipe : MemoryStream
    {
        public bool Gone { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Gone)
            {
                throw new IOException("The pipe has been ended.");
            }
            base.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Gone ? Task.FromException(new IOException("The pipe has been ended.")) : base.WriteAsync(buffer, offset, count, cancellationToken);
    }
}
