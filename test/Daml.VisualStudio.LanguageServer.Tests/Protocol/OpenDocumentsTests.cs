using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;
using static Daml.VisualStudio.LanguageServer.Tests.Protocol.LspFramingTests;

namespace Daml.VisualStudio.LanguageServer.Tests.Protocol;

// The adapter streams implement the array overloads (netstandard2.0 has no Memory overloads to
// override), so these test streams and calls stick to them on purpose.
#pragma warning disable CA1835, CA1844

// damlc stops serving a document that was closed and opened again, so the adapter never lets it
// see the close: the reopen becomes a full-text change, and the diagnostics Visual Studio dropped
// on close are handed back from what damlc last sent.
public class OpenDocumentsTests
{
    private const string Uri = "file:///M:/repo/daml/TypeError.daml";
    private static readonly LspAdapterSettings Settings = new() { MaxFrameBytes = 4096, MaxHeaderBytes = 256 };

    private static byte[] Open(int version, string text) =>
        Frame(new JObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didOpen", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = Uri, ["languageId"] = "daml", ["version"] = version, ["text"] = text } } }.ToString(Newtonsoft.Json.Formatting.None));

    private static readonly byte[] Close = Frame("""{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///M:/repo/daml/TypeError.daml"}}}""");

    private static byte[] Change(int version, string text) =>
        Frame(new JObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didChange", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = Uri, ["version"] = version }, ["contentChanges"] = new JArray { new JObject { ["text"] = text } } } }.ToString(Newtonsoft.Json.Formatting.None));

    private static readonly string Diagnostics = """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///m%3A/repo/daml/TypeError.daml","version":1,"diagnostics":[{"message":"Couldn't match","range":{"start":{"line":4,"character":8},"end":{"line":4,"character":19}}}]}}""";

    private static async Task<List<JObject>> SentToServerAsync(OpenDocuments documents, params byte[][] frames)
    {
        using var server = new MemoryStream();
        using (var toServer = new ClientToServerStream(server, Settings, new InitializeTracker(), NullDamlLogger.Instance, documents))
        {
            foreach (var frame in frames)
            {
                await toServer.WriteAsync(frame, 0, frame.Length);
            }
        }
        var reader = new LspFrameReader(new MemoryStream(server.ToArray()), Settings);
        var sent = new List<JObject>();
        while (await reader.ReadFrameAsync(CancellationToken.None) is { } body)
        {
            sent.Add(JObject.Parse(Encoding.UTF8.GetString(body)));
        }
        return sent;
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_close_never_reaches_the_server_and_the_reopen_becomes_a_full_text_change()
    {
        var sent = await SentToServerAsync(new OpenDocuments(), Open(1, "module A where"), Close, Open(7, "module A where\n"));

        sent.Select(m => (string)m["method"]!).ShouldBe(["textDocument/didOpen", "textDocument/didChange"]);
        var change = sent[1]["params"]!;
        ((string)change["textDocument"]!["uri"]!).ShouldBe(Uri);
        ((int)change["textDocument"]!["version"]!).ShouldBe(7);
        ((string)change["contentChanges"]![0]!["text"]!).ShouldBe("module A where\n");
        change["contentChanges"]![0]!["range"].ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_document_opened_for_the_first_time_is_passed_on_unchanged()
    {
        var sent = await SentToServerAsync(new OpenDocuments(), Open(1, "module A where"));

        ((string)sent.ShouldHaveSingleItem()["method"]!).ShouldBe("textDocument/didOpen");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task On_reopen_the_last_diagnostics_reach_the_client_while_the_server_stays_silent()
    {
        var documents = new OpenDocuments();
        using var server = new QueueStream();
        using var fromServer = new ServerToClientStream(server, Settings, new InitializeTracker(), NullDamlLogger.Instance, documents);
        server.Push(Frame(Diagnostics));
        Encoding.UTF8.GetString(await ReadFrameAsync(fromServer)).ShouldBe(Diagnostics);

        await SentToServerAsync(documents, Open(1, "x"), Close, Open(2, "x"));

        // Nothing more comes from the server; the copy is ours.
        var handedBack = JObject.Parse(Encoding.UTF8.GetString(await ReadFrameAsync(fromServer)));
        ((string)handedBack["method"]!).ShouldBe("textDocument/publishDiagnostics");
        JToken.DeepEquals(handedBack["params"]!["diagnostics"], JObject.Parse(Diagnostics)["params"]!["diagnostics"]).ShouldBeTrue();
    }

    // damlc stamps diagnostics with the document version they were computed for, and Visual
    // Studio ignores diagnostics for an older version than the one it has open.
    [Fact, Trait("Category", "Unit")]
    public async Task Diagnostics_handed_back_carry_the_version_of_the_reopened_document()
    {
        var documents = new OpenDocuments();
        using var server = new QueueStream();
        using var fromServer = new ServerToClientStream(server, Settings, new InitializeTracker(), NullDamlLogger.Instance, documents);
        var versioned = JObject.Parse(Diagnostics);
        versioned["params"]!["version"] = 1;
        server.Push(Frame(versioned.ToString(Newtonsoft.Json.Formatting.None)));
        await ReadFrameAsync(fromServer);

        await SentToServerAsync(documents, Open(1, "x"), Close, Open(5, "x"));

        var handedBack = JObject.Parse(Encoding.UTF8.GetString(await ReadFrameAsync(fromServer)));
        ((int)handedBack["params"]!["version"]!).ShouldBe(5);
        handedBack["params"]!["diagnostics"]!.Count().ShouldBe(1);
    }

    // multi-ide sends an empty, unversioned list for every known file whenever another file opens;
    // the package's own server never sends the errors again after it.
    [Fact, Trait("Category", "Unit")]
    public async Task The_coordinators_blanket_clear_is_neither_shown_nor_remembered()
    {
        var documents = new OpenDocuments();
        using var server = new QueueStream();
        using var fromServer = new ServerToClientStream(server, Settings, new InitializeTracker(), NullDamlLogger.Instance, documents);
        var versioned = JObject.Parse(Diagnostics);
        versioned["params"]!["version"] = 1;
        var blanketClear = """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///M:/repo/daml/TypeError.daml","diagnostics":[]}}""";
        var other = """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///M:/repo/daml/Other.daml","version":1,"diagnostics":[]}}""";
        server.Push(Frame(versioned.ToString(Newtonsoft.Json.Formatting.None)));
        server.Push(Frame(blanketClear));
        server.Push(Frame(other));

        await ReadFrameAsync(fromServer);
        var next = JObject.Parse(Encoding.UTF8.GetString(await ReadFrameAsync(fromServer)));
        ((string)next["params"]!["uri"]!).ShouldEndWith("Other.daml"); // the clear never arrived

        await SentToServerAsync(documents, Open(1, "x"), Close, Open(2, "x"));
        var handedBack = JObject.Parse(Encoding.UTF8.GetString(await ReadFrameAsync(fromServer)));
        handedBack["params"]!["diagnostics"]!.Count().ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_unversioned_clear_for_a_file_without_errors_passes_through()
    {
        var documents = new OpenDocuments();
        using var server = new QueueStream();
        using var fromServer = new ServerToClientStream(server, Settings, new InitializeTracker(), NullDamlLogger.Instance, documents);
        var clear = """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///M:/repo/daml/TypeError.daml","diagnostics":[]}}""";
        server.Push(Frame(clear));

        Encoding.UTF8.GetString(await ReadFrameAsync(fromServer)).ShouldBe(clear);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_earlier_diagnostics_a_reopen_delivers_nothing_extra()
    {
        var documents = new OpenDocuments();
        using var server = new QueueStream();
        using var fromServer = new ServerToClientStream(server, Settings, new InitializeTracker(), NullDamlLogger.Instance, documents);

        await SentToServerAsync(documents, Open(1, "x"), Close, Open(2, "x"));
        var next = ReadFrameAsync(fromServer);

        // Anything handed back on the reopen would be read before what the server sends next.
        server.Push(Frame("""{"jsonrpc":"2.0","method":"window/logMessage","params":{"type":3,"message":"hi"}}"""));
        ((string)JObject.Parse(Encoding.UTF8.GetString(await next))["method"]!).ShouldBe("window/logMessage");
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var reader = new LspFrameReader(new OneFrameStream(stream), Settings);
        return (await reader.ReadFrameAsync(CancellationToken.None))!;
    }

    // Reads from the inner stream one byte at a time, so a frame reader never takes more than its frame.
    private sealed class OneFrameStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A server's stdout that hands out what the test pushes and otherwise waits, like a pipe.
    private sealed class QueueStream : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = new();
        private byte[] _current = [];
        private int _position;

        public void Push(byte[] chunk) => _chunks.Add(chunk);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => Read(buffer, offset, count), cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position == _current.Length)
            {
                if (!_chunks.TryTake(out var next, Timeout.Infinite))
                {
                    return 0;
                }
                _current = next;
                _position = 0;
            }
            var n = Math.Min(count, _current.Length - _position);
            Array.Copy(_current, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chunks.CompleteAdding();
            }
            base.Dispose(disposing);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task An_edit_after_the_reopen_follows_it_with_its_own_version()
    {
        var sent = await SentToServerAsync(new OpenDocuments(), Open(1, "a"), Close, Open(7, "b"), Change(8, "c"));

        sent.Select(m => (string)m["method"]!).ShouldBe(["textDocument/didOpen", "textDocument/didChange", "textDocument/didChange"]);
        sent.Select(m => (int)m["params"]!["textDocument"]!["version"]!).ShouldBe([1, 7, 8]);
        ((string)sent[2]["params"]!["contentChanges"]![0]!["text"]!).ShouldBe("c");
    }

    [Fact, Trait("Category", "Unit")]
    public void Diagnostics_for_a_file_that_was_never_opened_pass_through()
    {
        var documents = new OpenDocuments();
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///M:/repo/daml/Other.daml","diagnostics":[]}}""");

        documents.FromServer(body).ShouldBeTrue();
    }
}
