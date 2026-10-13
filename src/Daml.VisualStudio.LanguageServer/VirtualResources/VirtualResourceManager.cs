using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Messages;
using StreamJsonRpc;

namespace Daml.VisualStudio.LanguageServer.VirtualResources;

/// <summary>
/// The windows that show virtual resources, one per resource. Implementations switch to the UI
/// thread themselves; only <see cref="ShowAsync"/> may bring a window up.
/// </summary>
public interface IVirtualResourceWindows
{
    /// <summary>Raised when the user closed the window of a resource; the resource is the argument.</summary>
    event EventHandler<string>? Closed;

    /// <summary>Opens the resource's window, or brings it up, and activates it.</summary>
    Task ShowAsync(string resource, string title, CancellationToken cancellationToken);

    /// <summary>Puts new contents into the resource's window without activating it.</summary>
    Task UpdateAsync(string resource, string html);

    /// <summary>Adds a note above the resource's contents.</summary>
    Task AddNoteAsync(string resource, string text);
}

/// <summary>
/// Opens script results and keeps their windows current. The server produces a resource while a
/// document with its address is open, so showing one opens that document over the session's
/// connection and closing the window closes it. After a restart the resources still on screen
/// are opened again, once, as soon as the new session is ready; until then, and for anything
/// that arrives from a session a resource isn't open in, notifications are dropped.
/// </summary>
public sealed class VirtualResourceManager : IVirtualResourceSink, IDisposable
{
    private readonly object _gate = new();
    private readonly IDamlRpcChannel _channel;
    private readonly IVirtualResourceWindows _windows;
    private readonly IDamlLogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<JsonRpc, string, string, Task> _notify;
    private readonly Dictionary<VirtualResourceUri, Resource> _resources = [];
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Creates the manager for the connections <paramref name="channel"/> hands out.</summary>
    public VirtualResourceManager(IDamlRpcChannel channel, IVirtualResourceWindows windows, IDamlLogger logger, TimeProvider? time = null)
        : this(channel, windows, logger, time, notify: null)
    {
    }

    // Tests see the didOpen/didClose notifications instead of a server.
    internal VirtualResourceManager(IDamlRpcChannel channel, IVirtualResourceWindows windows, IDamlLogger logger, TimeProvider? time, Func<JsonRpc, string, string, Task>? notify)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _notify = notify ?? NotifyAsync;
        _windows.Closed += OnWindowClosed;
    }

    /// <summary>
    /// Shows the results at <paramref name="resource"/> (a <c>daml://compiler</c> address from a
    /// lens) under <paramref name="title"/>: brings up its window if it has one, otherwise opens
    /// a window that waits for the results and asks the server for them.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="resource"/> isn't a virtual resource address.</exception>
    public async Task ShowAsync(string title, string resource, CancellationToken cancellationToken)
    {
        if (!VirtualResourceUri.TryParse(resource, out var key))
        {
            throw new ArgumentException($"'{resource}' isn't a script results address.", nameof(resource));
        }
        Resource entry;
        bool isNew;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            isNew = !_resources.TryGetValue(key!, out entry!);
            if (isNew)
            {
                // Registered before the server is asked, so a quick answer finds its window.
                entry = new Resource(resource);
                _resources[key!] = entry;
            }
        }
        await _windows.ShowAsync(entry.Address, title, cancellationToken).ConfigureAwait(false);
        if (isNew)
        {
            await OpenAsync(entry).ConfigureAwait(false);
        }
    }

    /// <summary>A session is ready: resources not yet open in it are opened in it.</summary>
    public void OnSessionReady(int sessionId)
    {
        if (!_channel.TryGet(out var rpc, out var current) || current != sessionId)
        {
            return; // a session that is already gone
        }
        List<Resource> reopen;
        lock (_gate)
        {
            // Switched now, so results the new session sends straight away are taken.
            reopen = _resources.Values.Where(r => r.Session != sessionId).ToList();
            foreach (var resource in reopen)
            {
                resource.Session = sessionId;
                resource.Contents = null;
            }
        }
        foreach (var resource in reopen)
        {
            Enqueue(() => SendAsync(rpc, "textDocument/didOpen", resource.Address));
        }
    }

    /// <inheritdoc />
    public void Changed(VirtualResourceChangedParams change)
    {
        if (change is null || Current(change.Uri) is not { } resource)
        {
            return;
        }
        lock (_gate)
        {
            resource.LastUpdate = _time.GetUtcNow().ToUnixTimeMilliseconds();
            if (resource.Contents == change.Contents)
            {
                return;
            }
            resource.Contents = change.Contents;
        }
        Enqueue(() => _windows.UpdateAsync(resource.Address, change.Contents));
    }

    /// <inheritdoc />
    public void Noted(VirtualResourceNoteParams note)
    {
        if (note is not null && Current(note.Uri) is { } resource)
        {
            Enqueue(() => _windows.AddNoteAsync(resource.Address, note.Note));
        }
    }

    /// <inheritdoc />
    public void Progressed(VirtualResourceProgressParams progress)
    {
        if (progress is null || Current(progress.Uri) is not { } resource)
        {
            return;
        }
        lock (_gate)
        {
            // Results newer than this run's start are already on screen.
            if (resource.LastUpdate > progress.StartedAt)
            {
                return;
            }
            resource.Contents = null;
        }
        var message = "<p>Virtual resource has been running for " + WebUtility.HtmlEncode(progress.MillisecondsPassed.ToString(System.Globalization.CultureInfo.InvariantCulture)) + " ms</p>";
        Enqueue(() => _windows.UpdateAsync(resource.Address, message));
    }

    // Completes once everything queued so far has reached the windows; tests wait on it.
    // The queue is ours and never captures a context, so waiting on it can't deadlock.
#pragma warning disable VSTHRD003
    internal Task IdleAsync()
    {
        lock (_gate)
        {
            return _tail;
        }
    }
#pragma warning restore VSTHRD003

    /// <summary>Stops handling notifications and window events.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _resources.Clear();
        }
        _windows.Closed -= OnWindowClosed;
    }

    // The resource the notification is about, if it has a window and is open in the session
    // the notification can have come from.
    private Resource? Current(string uri)
    {
        if (!VirtualResourceUri.TryParse(uri, out var key) || !_channel.TryGet(out _, out var session))
        {
            return null;
        }
        lock (_gate)
        {
            return !_disposed && _resources.TryGetValue(key!, out var resource) && resource.Session == session ? resource : null;
        }
    }

    // Without a session the resource waits; the next ready session opens it.
    private async Task OpenAsync(Resource resource)
    {
        if (!_channel.TryGet(out var rpc, out var session))
        {
            return;
        }
        lock (_gate)
        {
            if (_disposed || resource.Session == session || !_resources.ContainsValue(resource))
            {
                return; // already open there, or closed meanwhile
            }
            resource.Session = session;
        }
        await SendAsync(rpc, "textDocument/didOpen", resource.Address).ConfigureAwait(false);
    }

    private void OnWindowClosed(object? sender, string address)
    {
        if (!VirtualResourceUri.TryParse(address, out var key))
        {
            return;
        }
        Resource? closed;
        lock (_gate)
        {
            if (!_resources.TryGetValue(key!, out closed))
            {
                return;
            }
            _resources.Remove(key!);
        }
        // Only the session it is open in knows about it; a later session never opened it.
        if (_channel.TryGet(out var rpc, out var session) && session == closed!.Session)
        {
            var closedAddress = closed.Address;
            Enqueue(() => SendAsync(rpc, "textDocument/didClose", closedAddress));
        }
    }

    private async Task SendAsync(JsonRpc rpc, string method, string address)
    {
        try
        {
            await _notify(rpc, method, address).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or InvalidOperationException)
        {
            // The session is ending; the next one opens what is still on screen.
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"{method} for script results not sent: {ex.Message}");
        }
    }

    // Window work runs one piece at a time, in the order the server sent it.
    private void Enqueue(Func<Task> work)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(_ => RunAsync(work), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Error, "Couldn't show script results.", ex);
        }
    }

    private static Task NotifyAsync(JsonRpc rpc, string method, string address) =>
        method == "textDocument/didOpen"
            ? rpc.NotifyWithParameterObjectAsync(method, new { textDocument = new { uri = address, languageId = "", version = 0, text = "" } })
            : rpc.NotifyWithParameterObjectAsync(method, new { textDocument = new { uri = address } });

    private sealed class Resource(string address)
    {
        public string Address { get; } = address;

        // The session it was opened in; 0 before any.
        public int Session { get; set; }

        public string? Contents { get; set; }

        public long LastUpdate { get; set; } = long.MinValue;
    }
}
