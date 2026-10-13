using System;
using System.IO;
using Daml.VisualStudio.LanguageServer.Hosting;

namespace Daml.VisualStudio.LanguageClient;

/// <summary>
/// Watches the workspace root for the changes the server won't notice by itself: manifest
/// edits, and the package database vanishing under a clean. What it sees goes to the host,
/// which decides whether a restart is due.
/// </summary>
internal sealed class WorkspaceWatcher : IDisposable
{
    // A build or clean touches thousands of entries under .daml at once, more than the default
    // 8 KB buffer holds; when events are lost anyway, the host is told.
    private const int BufferSize = 64 * 1024;

    private readonly LanguageServerHost _host;
    private readonly object _gate = new();
    private FileSystemWatcher? _files;
    private FileSystemWatcher? _folders;
    private string? _root;

    public WorkspaceWatcher(LanguageServerHost host) => _host = host;

    /// <summary>Watches <paramref name="root"/>; the same root again, or none, changes nothing.</summary>
    public void Watch(string? root)
    {
        lock (_gate)
        {
            if (root is null || string.Equals(root, _root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            {
                return;
            }
            _files?.Dispose();
            _folders?.Dispose();
            _root = root;

            // One filter only on .NET Framework; the host ignores YAML files that aren't manifests.
            _files = new FileSystemWatcher(root, "*.yaml") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, InternalBufferSize = BufferSize };
            _files.Changed += (_, e) => _host.OnFileChanged(e.FullPath);
            _files.Created += (_, e) => _host.OnFileChanged(e.FullPath);
            _files.Deleted += (_, e) => _host.OnFileChanged(e.FullPath);
            _files.Renamed += (_, e) => _host.OnFileChanged(e.FullPath);
            _files.Error += (_, _) => _host.OnWatcherOverflow();
            _files.EnableRaisingEvents = true;

            // dpm clean deletes the package database damlc reads library sources from.
            _folders = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName, InternalBufferSize = BufferSize };
            _folders.Deleted += (_, e) => _host.OnDirectoryRemoved(e.FullPath);
            _folders.Error += (_, _) => _host.OnWatcherOverflow();
            _folders.EnableRaisingEvents = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _files?.Dispose();
            _files = null;
            _folders?.Dispose();
            _folders = null;
            _root = null;
        }
    }
}
