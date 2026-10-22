using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Equilibrium.Daml.TestHelpers;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

// The array and synchronous overloads are what a net48 client calls; they are exercised on purpose.
#pragma warning disable CA1835, VSTHRD103

namespace Equilibrium.Daml.TestHelpers.Tests;

public class LspTranscriptRecorderTests
{
    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Frames_are_recorded_in_both_directions_however_the_bytes_are_split()
    {
        var recorder = new LspTranscriptRecorder(new TranscriptRedactor([]));
        var sent = new MemoryStream();
        var toServer = recorder.Outgoing(sent);
        var fromServer = recorder.Incoming(new MemoryStream(Frame("""{"id":1,"result":"ż"}""")));

        // A frame written one byte at a time, then two frames in one write.
        foreach (var b in Frame("""{"id":1,"method":"initialize"}"""))
        {
            await toServer.WriteAsync(new[] { b }, 0, 1);
        }
        var buffer = new byte[3];
        while (await fromServer.ReadAsync(buffer, 0, buffer.Length) > 0)
        {
        }
        var two = Frame("""{"method":"initialized"}""").Concat(Frame("""{"method":"exit"}""")).ToArray();
        toServer.Write(two, 0, two.Length);

        var lines = recorder.Lines().Select(JObject.Parse).ToList();
        lines.Select(l => (string?)l["direction"]).ShouldBe(["client", "server", "client", "client"]);
        lines.Select(l => (string?)l["message"]!["method"] ?? (string?)l["message"]!["result"]).ShouldBe(["initialize", "ż", "initialized", "exit"]);
        sent.ToArray().Length.ShouldBe(Frame("""{"id":1,"method":"initialize"}""").Length + two.Length, "bytes pass through unchanged");
    }

    [Fact, Trait("Category", "Unit")]
    public void Saved_transcripts_are_redacted_one_message_per_line()
    {
        var recorder = new LspTranscriptRecorder(new TranscriptRedactor([(@"C:\work\app", "${ROOT}")]));
        var frame = Frame("""{"method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///C:/work/app/Main.daml","text":"module Main"}}}""");
        recorder.Outgoing(new MemoryStream()).Write(frame, 0, frame.Length);
        var path = Path.Combine(Path.GetTempPath(), "daml-transcript-test", "t.jsonl");

        recorder.Save(path);

        var saved = File.ReadAllText(path);
        saved.ShouldContain("file:///${ROOT}/Main.daml");
        saved.ShouldNotContain("module Main");
        saved.Split('\n').Count(l => l.Length > 0).ShouldBe(1);
    }
}
