using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;

namespace Equilibrium.Daml.VisualStudio.LanguageClient;

/// <summary>
/// Watches the workspace root, and the solution's packages outside it, for the changes the server
/// won't notice by itself: manifest edits, and the package database vanishing under a clean. What
/// it sees goes to the host, which decides whether a restart is due.
/// </summary>
internal sealed class WorkspaceWatcher : IDisposable
{
    // A build or clean touches thousands of entries under .daml at once, more than the default
    // 8 KB buffer holds; when events are lost anyway, the host is told.
    private const int BufferSize = 64 * 1024;

    private readonly LanguageServerHost _host;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private List<string> _folders = [];

    public WorkspaceWatcher(LanguageServerHost host) => _host = host;

    /// <summary>
    /// Watches <paramref name="root"/> and those of <paramref name="packages"/> that lie outside it;
    /// the same folders again, or no root, change nothing.
    /// </summary>
    public void Watch(string? root, IEnumerable<string>? packages = null)
    {
        if (root is null || !Directory.Exists(root))
        {
            return;
        }
        var folders = new List<string> { root };
        folders.AddRange((packages ?? []).Where(p => !DamlPath.IsSameOrUnder(p, root) && Directory.Exists(p)).Distinct(DamlPath.Comparer));
        lock (_gate)
        {
            if (folders.SequenceEqual(_folders, DamlPath.Comparer))
            {
                return;
            }
            Stop();
            _folders = folders;
            foreach (var folder in folders)
            {
                WatchFolder(folder);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Stop();
            _folders = [];
        }
    }

    private void WatchFolder(string folder)
    {
        // One filter only on .NET Framework; the host ignores YAML files that aren't manifests.
        var files = new FileSystemWatcher(folder, "*.yaml") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, InternalBufferSize = BufferSize };
        files.Changed += (_, e) => _host.OnFileChanged(e.FullPath);
        files.Created += (_, e) => _host.OnFileChanged(e.FullPath);
        files.Deleted += (_, e) => _host.OnFileChanged(e.FullPath);
        files.Renamed += (_, e) => _host.OnFileChanged(e.FullPath);
        files.Error += (_, _) => _host.OnWatcherOverflow();
        files.EnableRaisingEvents = true;
        _watchers.Add(files);

        // dpm clean deletes the package database damlc reads library sources from.
        var directories = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName, InternalBufferSize = BufferSize };
        directories.Deleted += (_, e) => _host.OnDirectoryRemoved(e.FullPath);
        directories.Error += (_, _) => _host.OnWatcherOverflow();
        directories.EnableRaisingEvents = true;
        _watchers.Add(directories);
    }

    private void Stop()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
