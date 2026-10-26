using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>
/// Reads LSP messages (<c>Content-Length</c> header, empty line, body) from a stream. Each body
/// is read into one exactly sized array; nothing beyond the limits is ever allocated.
/// </summary>
internal sealed class LspFrameReader
{
    private readonly Stream _stream;
    private readonly LspAdapterSettings _settings;
    private readonly byte[] _buffer;
    private int _start;
    private int _end;

    /// <summary>Creates a reader over <paramref name="stream"/>.</summary>
    public LspFrameReader(Stream stream, LspAdapterSettings settings)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _buffer = new byte[Math.Max(4096, settings.MaxHeaderBytes + 4)];
    }

    /// <summary>The next message body, or <c>null</c> when the stream ends cleanly between messages.</summary>
    /// <exception cref="LspFramingException">Bad or oversized header, or the stream ended mid-message.</exception>
    public Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken) => ReadFrameCoreAsync(async: true, cancellationToken);

    /// <summary>Synchronous <see cref="ReadFrameAsync"/>.</summary>
    public byte[]? ReadFrame()
    {
        // With async: false nothing inside awaits anything incomplete.
#pragma warning disable VSTHRD002
        return ReadFrameCoreAsync(async: false, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    // One body for both ReadFrame and ReadFrameAsync: the blocking reads only run when async is false.
#pragma warning disable VSTHRD103
    private async Task<byte[]?> ReadFrameCoreAsync(bool async, CancellationToken cancellationToken)
    {
        int headerLength;
        while ((headerLength = LspHeader.FindEnd(_buffer, _start, _end - _start, _settings.MaxHeaderBytes)) < 0)
        {
            if (_end - _start >= _settings.MaxHeaderBytes)
            {
                throw new LspFramingException($"A message header is longer than {_settings.MaxHeaderBytes} bytes or isn't ended by an empty line.");
            }
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var read = async
                ? await _stream.ReadAsync(_buffer, _end, _buffer.Length - _end, cancellationToken).ConfigureAwait(false)
                : _stream.Read(_buffer, _end, _buffer.Length - _end);
            if (read == 0)
            {
                if (_end == _start)
                {
                    return null;
                }
                throw new LspFramingException("The stream ended inside a message header.");
            }
            _end += read;
        }

        var length = LspHeader.ParseContentLength(_buffer, _start, headerLength, _settings.MaxFrameBytes);
        _start += headerLength;

        var body = new byte[length];
        var buffered = Math.Min(length, _end - _start);
        Buffer.BlockCopy(_buffer, _start, body, 0, buffered);
        _start += buffered;
        var filled = buffered;
        while (filled < length)
        {
            var read = async
                ? await _stream.ReadAsync(body, filled, length - filled, cancellationToken).ConfigureAwait(false)
                : _stream.Read(body, filled, length - filled);
            if (read == 0)
            {
                throw new LspFramingException($"The stream ended {filled} bytes into a {length}-byte message body.");
            }
            filled += read;
        }
        return body;
    }
#pragma warning restore VSTHRD103
}

/// <summary>Writes LSP messages.</summary>
internal static class LspFrameWriter
{
    /// <summary>Writes <paramref name="body"/> with its <c>Content-Length</c> header.</summary>
    public static async Task WriteAsync(Stream stream, byte[] body, CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        if (body is null)
        {
            throw new ArgumentNullException(nameof(body));
        }
        var header = LspHeader.Format(body.Length);
        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="WriteAsync"/>.</summary>
    public static void Write(Stream stream, byte[] body)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        if (body is null)
        {
            throw new ArgumentNullException(nameof(body));
        }
        var header = LspHeader.Format(body.Length);
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }
}

internal static class LspHeader
{
    // Length of the header block including its terminating empty line, or -1 if it isn't all there.
    /// <summary>Whether the first <paramref name="count"/> bytes end with the empty line that closes a header.</summary>
    public static bool EndsWithBlankLine(byte[] data, int count) =>
        count >= 4 && data[count - 1] == '\n' && data[count - 2] == '\r' && data[count - 3] == '\n' && data[count - 4] == '\r';

    public static int FindEnd(byte[] data, int offset, int count, int maxHeaderBytes)
    {
        var limit = Math.Min(count, maxHeaderBytes + 4);
        for (var i = 3; i < limit; i++)
        {
            if (data[offset + i] == '\n' && data[offset + i - 1] == '\r' && data[offset + i - 2] == '\n' && data[offset + i - 3] == '\r')
            {
                return i + 1;
            }
        }
        return -1;
    }

    public static int ParseContentLength(byte[] data, int offset, int headerLength, int maxFrameBytes)
    {
        int? contentLength = null;
        var text = Encoding.ASCII.GetString(data, offset, headerLength - 4);
        foreach (var line in text.Split(["\r\n"], StringSplitOptions.None))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new LspFramingException($"Malformed message header line '{line}'.");
            }
            if (!line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Content-Type, or something we don't need
            }
            if (contentLength is not null)
            {
                throw new LspFramingException("Content-Length appears more than once in a message header.");
            }
            var value = line.Substring(colon + 1).Trim();
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new LspFramingException($"Invalid Content-Length '{value}'.");
            }
            if (parsed > maxFrameBytes)
            {
                throw new LspFramingException($"A message of {parsed} bytes exceeds the limit of {maxFrameBytes} bytes.");
            }
            contentLength = (int)parsed;
        }
        return contentLength ?? throw new LspFramingException("A message header has no Content-Length.");
    }

    public static byte[] Format(int contentLength) =>
        Encoding.ASCII.GetBytes("Content-Length: " + contentLength.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
}
