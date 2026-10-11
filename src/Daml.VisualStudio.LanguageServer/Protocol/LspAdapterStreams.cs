using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;

namespace Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>
/// What the LSP client writes to: reassembles messages however the writes split them, makes
/// each acceptable to damlc and writes it on to the server's stdin with a fresh
/// <c>Content-Length</c>. Expects one writer at a time, which is how JSON-RPC writes.
/// </summary>
public sealed class ClientToServerStream : Stream
{
    private readonly Stream _server;
    private readonly LspAdapterSettings _settings;
    private readonly InitializeTracker _tracker;
    private readonly IDamlLogger _logger;
    private readonly byte[] _header;
    private int _headerCount;
    private byte[]? _body;
    private int _bodyFilled;

    /// <summary>Creates the stream over the server's standard input.</summary>
    public ClientToServerStream(Stream server, LspAdapterSettings settings, InitializeTracker tracker, IDamlLogger logger)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _header = new byte[settings.MaxHeaderBytes + 4];
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteCoreAsync(buffer, offset, count, async: true, cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        // With async: false nothing inside awaits anything incomplete.
#pragma warning disable VSTHRD002
        WriteCoreAsync(buffer, offset, count, async: false, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    /// <inheritdoc />
    public override void Flush() => _server.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _server.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task WriteCoreAsync(byte[] buffer, int offset, int count, bool async, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (_body is null)
            {
                // Headers are a few dozen bytes; taking them a byte at a time keeps this simple.
                _header[_headerCount++] = buffer[offset++];
                count--;
                var headerLength = LspHeader.FindEnd(_header, 0, _headerCount, _settings.MaxHeaderBytes);
                if (headerLength < 0)
                {
                    if (_headerCount == _header.Length)
                    {
                        throw Fail(new LspFramingException($"A message header from the client is longer than {_settings.MaxHeaderBytes} bytes."));
                    }
                    continue;
                }
                int length;
                try
                {
                    length = LspHeader.ParseContentLength(_header, 0, headerLength, _settings.MaxFrameBytes);
                }
                catch (LspFramingException ex)
                {
                    throw Fail(ex);
                }
                _headerCount = 0;
                _body = new byte[length];
                _bodyFilled = 0;
            }
            else
            {
                var take = Math.Min(count, _body.Length - _bodyFilled);
                Buffer.BlockCopy(buffer, offset, _body, _bodyFilled, take);
                _bodyFilled += take;
                offset += take;
                count -= take;
            }

            if (_body is not null && _bodyFilled == _body.Length)
            {
                var body = _body;
                _body = null;
                await ForwardAsync(body, async, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ForwardAsync(byte[] body, bool async, CancellationToken cancellationToken)
    {
        var message = ClientMessageSanitizer.Sanitize(body);
        if (message.Action == SanitizeAction.Drop)
        {
            if (_logger.IsEnabled(DamlLogLevel.Trace))
            {
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Trace, "Dropped a client notification damlc doesn't understand (NotificationReceived).");
            }
            return;
        }
        if (message.InitializeRequestId is { } id)
        {
            _tracker.Expect(id);
        }
        if (async)
        {
            await LspFrameWriter.WriteAsync(_server, message.Body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            LspFrameWriter.Write(_server, message.Body);
        }
    }

    private LspFramingException Fail(LspFramingException error)
    {
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "The language client sent a malformed message; closing the connection.", error);
        return error;
    }
}

/// <summary>
/// What the LSP client reads from: the server's messages, checked against the framing rules and
/// size limit, passed on unchanged (with a normalised header) while watching for the reply to
/// <c>initialize</c>. A framing error surfaces as an <see cref="IOException"/>, which ends the
/// connection.
/// </summary>
public sealed class ServerToClientStream : Stream
{
    private readonly Stream _server;
    private readonly LspFrameReader _reader;
    private readonly InitializeTracker _tracker;
    private readonly IDamlLogger _logger;
    private byte[] _header = [];
    private byte[] _body = [];
    private int _headerPosition;
    private int _bodyPosition;

    /// <summary>Creates the stream over the server's standard output.</summary>
    public ServerToClientStream(Stream server, LspAdapterSettings settings, InitializeTracker tracker, IDamlLogger logger)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _reader = new LspFrameReader(server, settings ?? throw new ArgumentNullException(nameof(settings)));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadCoreAsync(buffer, offset, count, async: true, cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        // With async: false nothing inside awaits anything incomplete.
#pragma warning disable VSTHRD002
        return ReadCoreAsync(buffer, offset, count, async: false, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, bool async, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return 0;
        }
        if (_headerPosition == _header.Length && _bodyPosition == _body.Length)
        {
            byte[]? body;
            try
            {
                body = async ? await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false) : _reader.ReadFrame();
            }
            catch (LspFramingException ex)
            {
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "The language server sent a malformed message; closing the connection.", ex);
                throw;
            }
            if (body is null)
            {
                return 0;
            }
            _tracker.Inspect(body);
            _header = LspHeader.Format(body.Length);
            _body = body;
            _headerPosition = 0;
            _bodyPosition = 0;
        }

        if (_headerPosition < _header.Length)
        {
            var take = Math.Min(count, _header.Length - _headerPosition);
            Buffer.BlockCopy(_header, _headerPosition, buffer, offset, take);
            _headerPosition += take;
            return take;
        }
        var bodyTake = Math.Min(count, _body.Length - _bodyPosition);
        Buffer.BlockCopy(_body, _bodyPosition, buffer, offset, bodyTake);
        _bodyPosition += bodyTake;
        return bodyTake;
    }
}
