using System;
using System.Collections.Concurrent;
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
    private readonly OpenDocuments? _documents;
    private readonly Action<Exception>? _onFailure;
    private int _headerCount;
    private byte[]? _body;
    private int _bodyFilled;

    /// <summary>Creates the stream over the server's standard input.</summary>
    /// <param name="server">The server's standard input.</param>
    /// <param name="settings">Size limits.</param>
    /// <param name="tracker">Learns the id of the <c>initialize</c> request.</param>
    /// <param name="logger">Where framing problems are logged.</param>
    /// <param name="documents">Keeps closes away from the server; shared with the session's <see cref="ServerToClientStream"/>.</param>
    /// <param name="onFailure">Told when a framing problem ends the connection.</param>
    public ClientToServerStream(Stream server, LspAdapterSettings settings, InitializeTracker tracker, IDamlLogger logger, OpenDocuments? documents = null, Action<Exception>? onFailure = null)
    {
        _documents = documents;
        _onFailure = onFailure;
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
                // The header ends with an empty line; only the last four bytes can complete it.
                var headerLength = LspHeader.EndsWithBlankLine(_header, _headerCount) ? _headerCount : -1;
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
        var outgoing = _documents is null ? message.Body : _documents.FromClient(message.Body);
        if (outgoing is null)
        {
            return; // a close the server must not see
        }
        if (async)
        {
            await LspFrameWriter.WriteAsync(_server, outgoing, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            LspFrameWriter.Write(_server, outgoing);
        }
    }

    private LspFramingException Fail(LspFramingException error)
    {
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "The language client sent a malformed message; closing the connection.", error);
        _onFailure?.Invoke(error);
        return error;
    }
}

/// <summary>
/// What the LSP client reads from: the server's messages, checked against the framing rules and
/// size limit, passed on unchanged (with a normalised header) while watching for the reply to
/// <c>initialize</c>. A background reader queues them, so a message handed back by
/// <see cref="OpenDocuments"/> reaches the client even while the server stays silent. A framing
/// error surfaces as an <see cref="IOException"/>, which ends the connection.
/// </summary>
public sealed class ServerToClientStream : Stream
{
    private readonly Stream _server;
    private readonly LspFrameReader _reader;
    private readonly InitializeTracker _tracker;
    private readonly IDamlLogger _logger;
    private readonly OpenDocuments? _documents;
    private readonly Action<Exception>? _onFailure;
    private readonly ConcurrentQueue<byte[]> _frames = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _pump;
    private Exception? _failure;
    private byte[] _header = [];
    private byte[] _body = [];
    private int _headerPosition;
    private int _bodyPosition;

    /// <summary>Creates the stream over the server's standard output.</summary>
    /// <param name="server">The server's standard output.</param>
    /// <param name="settings">Size limits.</param>
    /// <param name="tracker">Spots the reply to <c>initialize</c>.</param>
    /// <param name="logger">Where framing problems are logged.</param>
    /// <param name="documents">Remembers diagnostics for reopened documents; shared with the session's <see cref="ClientToServerStream"/>.</param>
    /// <param name="onFailure">Told when a framing problem ends the connection.</param>
    public ServerToClientStream(Stream server, LspAdapterSettings settings, InitializeTracker tracker, IDamlLogger logger, OpenDocuments? documents = null, Action<Exception>? onFailure = null)
    {
        _onFailure = onFailure;
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _reader = new LspFrameReader(server, settings ?? throw new ArgumentNullException(nameof(settings)));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _documents = documents;
        if (documents is not null)
        {
            documents.Redeliver += Enqueue;
        }
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
            if (_documents is not null)
            {
                _documents.Redeliver -= Enqueue;
            }
            _stop.Cancel();
            _server.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Enqueue(byte[] body)
    {
        _frames.Enqueue(body);
        _available.Release();
    }

    private void StartPump()
    {
        lock (_gate)
        {
            _pump ??= Task.Run(PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _reader.ReadFrameAsync(_stop.Token).ConfigureAwait(false) is { } body)
            {
                _tracker.Inspect(body);
                if (_documents?.FromServer(body) == false)
                {
                    continue;
                }
                Enqueue(body);
            }
        }
        catch (LspFramingException ex)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, "The language server sent a malformed message; closing the connection.", ex);
            _failure = ex;
            _onFailure?.Invoke(ex);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The server went away.
        }
        _available.Release();
    }

    // One body for Read and ReadAsync: the blocking wait only runs when async is false.
#pragma warning disable VSTHRD103
    private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, bool async, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return 0;
        }
        if (_headerPosition == _header.Length && _bodyPosition == _body.Length)
        {
            StartPump();
            if (async)
            {
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _available.Wait(cancellationToken);
            }
            if (!_frames.TryDequeue(out var body))
            {
                // The end: let any later read see it too.
                _available.Release();
                if (_failure is not null)
                {
                    throw new IOException(_failure.Message, _failure);
                }
                return 0;
            }
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
#pragma warning restore VSTHRD103
}
