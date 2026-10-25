using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Protocol;

public class LspFramingTests
{
    private static readonly LspAdapterSettings Settings = new() { MaxFrameBytes = 1024, MaxHeaderBytes = 256 };

    internal static byte[] Frame(string json, string? extraHeader = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {body.Length}\r\n" + (extraHeader is null ? "" : extraHeader + "\r\n") + "\r\n";
        var frame = new byte[Encoding.ASCII.GetByteCount(header) + body.Length];
        Encoding.ASCII.GetBytes(header, 0, header.Length, frame, 0);
        body.CopyTo(frame, frame.Length - body.Length);
        return frame;
    }

    private static LspFrameReader Reader(byte[] data, int chunk = int.MaxValue) => new(new TrickleStream(data, chunk), Settings);

    [Theory, Trait("Category", "Unit")]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(7)]
    public async Task Frames_are_read_whole_however_the_bytes_arrive(int chunk)
    {
        var data = Concat(Frame("""{"id":1}"""), Frame("""{"id":2,"x":"zażółć"}""", "Content-Type: application/vscode-jsonrpc; charset=utf-8"));
        var reader = Reader(data, chunk);

        Encoding.UTF8.GetString((await reader.ReadFrameAsync(CancellationToken.None))!).ShouldBe("""{"id":1}""");
        Encoding.UTF8.GetString((await reader.ReadFrameAsync(CancellationToken.None))!).ShouldBe("""{"id":2,"x":"zażółć"}""");
        (await reader.ReadFrameAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("Content-Type: x\r\n\r\n{}", "Content-Length")]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}", "more than once")]
    [InlineData("Content-Length: -1\r\n\r\n", "Content-Length")]
    [InlineData("Content-Length: 1x\r\n\r\n", "Content-Length")]
    [InlineData("Content-Length 2\r\n\r\n{}", "header")]
    [InlineData("Content-Length: 2\n\n{}", "header")]
    [InlineData("Content-Length: 5000\r\n\r\n{}", "limit")]
    // Refused before anything is allocated for it: a buffer that size would fail with OutOfMemoryException instead.
    [InlineData("Content-Length: 2147483647\r\n\r\n{}", "limit")]
    public async Task Bad_headers_are_refused(string raw, string expected)
    {
        var error = await Should.ThrowAsync<LspFramingException>(() => Reader(Encoding.ASCII.GetBytes(raw)).ReadFrameAsync(CancellationToken.None));

        error.Message.ShouldContain(expected);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_header_that_never_ends_is_refused_at_the_header_limit()
    {
        var raw = Encoding.ASCII.GetBytes("Content-Length: 2\r\nX-Filler: " + new string('a', 1000));

        var error = await Should.ThrowAsync<LspFramingException>(() => Reader(raw).ReadFrameAsync(CancellationToken.None));

        error.Message.ShouldContain("header");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_frame_cut_off_by_the_end_of_the_stream_is_an_error()
    {
        var frame = Frame("""{"id":1,"result":null}""");
        var cut = new byte[frame.Length - 3];
        Array.Copy(frame, cut, cut.Length);

        var error = await Should.ThrowAsync<LspFramingException>(() => Reader(cut).ReadFrameAsync(CancellationToken.None));

        error.Message.ShouldContain("ended");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Writing_a_frame_puts_the_body_length_in_the_header()
    {
        using var output = new MemoryStream();
        var body = Encoding.UTF8.GetBytes("""{"x":"ł"}""");

        await LspFrameWriter.WriteAsync(output, body, CancellationToken.None);

        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("Content-Length: 10\r\n\r\n{\"x\":\"ł\"}");
    }

    internal static byte[] Concat(params byte[][] parts)
    {
        using var all = new MemoryStream();
        foreach (var part in parts)
        {
            all.Write(part, 0, part.Length);
        }
        return all.ToArray();
    }

    // Hands out at most `chunk` bytes per read, like a pipe under load.
    internal sealed class TrickleStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _position;

        public TrickleStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, _chunk), _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
