using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Daml.TestHelpers;

/// <summary>
/// Records the LSP messages passing through a client's streams, both ways, in the order they
/// pass. The streams it hands out forward every byte unchanged; whole frames are cut out of the
/// copy and kept, redacted, one JSON line per message:
/// <c>{"direction":"client"|"server","message":{...}}</c>.
/// </summary>
public sealed class LspTranscriptRecorder
{
    private readonly TranscriptRedactor _redactor;
    private readonly List<string> _lines = new();

    /// <summary>Creates a recorder that redacts with <paramref name="redactor"/>.</summary>
    public LspTranscriptRecorder(TranscriptRedactor redactor) => _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

    /// <summary>Wraps the stream the client writes to the server.</summary>
    public Stream Outgoing(Stream inner) => new TapStream(inner, new FrameCollector(this, "client"));

    /// <summary>Wraps the stream the client reads from the server.</summary>
    public Stream Incoming(Stream inner) => new TapStream(inner, new FrameCollector(this, "server"));

    /// <summary>The redacted lines so far.</summary>
    public IReadOnlyList<string> Lines()
    {
        lock (_lines)
        {
            return _lines.ToArray();
        }
    }

    /// <summary>Writes the transcript to <paramref name="path"/>, creating its folder.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, string.Concat(Lines().Select(l => l + "\n")), new UTF8Encoding(false));
    }

    private void Add(string direction, byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        JToken message;
        try
        {
            message = _redactor.Redact(JObject.Parse(text));
        }
        catch (JsonReaderException)
        {
            message = new JValue(_redactor.RedactText(text));
        }
        var line = new JObject { ["direction"] = direction, ["message"] = message }.ToString(Formatting.None);
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    // Cuts Content-Length frames out of a byte stream that may arrive in any pieces.
    private sealed class FrameCollector(LspTranscriptRecorder recorder, string direction)
    {
        private static readonly byte[] HeaderEnd = Encoding.ASCII.GetBytes("\r\n\r\n");
        private readonly List<byte> _buffer = new();

        public void Feed(byte[] bytes, int offset, int count)
        {
            lock (_buffer)
            {
                for (var i = 0; i < count; i++)
                {
                    _buffer.Add(bytes[offset + i]);
                }
                while (TryTakeFrame(out var body))
                {
                    recorder.Add(direction, body);
                }
            }
        }

        private bool TryTakeFrame(out byte[] body)
        {
            body = Array.Empty<byte>();
            var end = IndexOf(HeaderEnd);
            if (end < 0)
            {
                return false;
            }
            var header = Encoding.ASCII.GetString(_buffer.Take(end).ToArray());
            var length = header.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(new[] { ':' }, 2))
                .Where(pair => pair.Length == 2 && pair[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(pair => int.Parse(pair[1].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                .FirstOrDefault();
            var start = end + HeaderEnd.Length;
            if (_buffer.Count < start + length)
            {
                return false;
            }
            body = _buffer.Skip(start).Take(length).ToArray();
            _buffer.RemoveRange(0, start + length);
            return true;
        }

        private int IndexOf(byte[] pattern)
        {
            for (var i = 0; i + pattern.Length <= _buffer.Count; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length && match; j++)
                {
                    match = _buffer[i + j] == pattern[j];
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    // Passes everything through and shows the collector a copy of what was read or written. The
    // array overloads forward to the same overloads of the inner stream on purpose.
#pragma warning disable CA1835
    private sealed class TapStream(Stream inner, FrameCollector collector) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            collector.Feed(buffer, offset, read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            collector.Feed(buffer, offset, read);
            return read;
        }

#if NET
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            var copy = buffer.Slice(0, read).ToArray();
            collector.Feed(copy, 0, copy.Length);
            return read;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copy = buffer.ToArray();
            collector.Feed(copy, 0, copy.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
#endif

        public override void Write(byte[] buffer, int offset, int count)
        {
            collector.Feed(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            collector.Feed(buffer, offset, count);
            await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
#pragma warning restore CA1835
}
