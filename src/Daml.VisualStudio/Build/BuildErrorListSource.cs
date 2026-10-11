using System;
using System.Collections.Generic;
using Daml.Build.Core.Diagnostics;
using Daml.VisualStudio.Core.Build;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Daml.VisualStudio.Build;

/// <summary>
/// Feeds the Error List from a <see cref="BuildDiagnosticStore"/>. Entries carry the build
/// tool "Daml Build", so they can be told apart from (and filtered against) the language
/// server's live diagnostics, like Build and IntelliSense entries for C#. The Error List opens
/// the document at Line/Column itself.
/// </summary>
internal sealed class BuildErrorListSource : ITableDataSource, IDisposable
{
    public const string BuildToolName = "Daml Build";

    public static readonly string[] Columns =
    [
        StandardTableKeyNames.ErrorSeverity,
        StandardTableKeyNames.BuildTool,
        StandardTableKeyNames.ErrorSource,
        StandardTableKeyNames.DocumentName,
        StandardTableKeyNames.Line,
        StandardTableKeyNames.Column,
        StandardTableKeyNames.Text,
    ];

    private readonly BuildDiagnosticStore _store;
    private readonly object _gate = new();
    private readonly List<ITableDataSink> _sinks = new();
    private Snapshot _snapshot;

    public BuildErrorListSource(BuildDiagnosticStore store)
    {
        _store = store;
        _snapshot = new Snapshot(store.Current, store.Version);
        _store.Changed += OnStoreChanged;
    }

    public string SourceTypeIdentifier => StandardTableDataSources.ErrorTableDataSource;

    public string Identifier => "Daml.Build";

    public string DisplayName => BuildToolName;

    public IDisposable Subscribe(ITableDataSink sink)
    {
        lock (_gate)
        {
            _sinks.Add(sink);
            sink.AddSnapshot(_snapshot, removeAllSnapshots: true);
        }
        return new Subscription(this, sink);
    }

    public void Dispose() => _store.Changed -= OnStoreChanged;

    private void OnStoreChanged(object sender, EventArgs e)
    {
        // Sinks are free-threaded; the lock only keeps snapshot replacement in order.
        lock (_gate)
        {
            var previous = _snapshot;
            _snapshot = new Snapshot(_store.Current, _store.Version);
            foreach (var sink in _sinks)
            {
                sink.ReplaceSnapshot(previous, _snapshot);
            }
        }
    }

    private void Unsubscribe(ITableDataSink sink)
    {
        lock (_gate)
        {
            _sinks.Remove(sink);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly BuildErrorListSource _source;
        private readonly ITableDataSink _sink;

        public Subscription(BuildErrorListSource source, ITableDataSink sink)
        {
            _source = source;
            _sink = sink;
        }

        public void Dispose() => _source.Unsubscribe(_sink);
    }

    internal sealed class Snapshot : ITableEntriesSnapshot
    {
        private readonly IReadOnlyList<BuildDiagnostic> _entries;

        public Snapshot(IReadOnlyList<BuildDiagnostic> entries, int version)
        {
            _entries = entries;
            VersionNumber = version;
        }

        public int Count => _entries.Count;

        public int VersionNumber { get; }

        // Every build replaces the whole list; entries aren't tracked from one snapshot to the next.
        public int IndexOf(int currentIndex, ITableEntriesSnapshot newSnapshot) => -1;

        public void StartCaching()
        {
        }

        public void StopCaching()
        {
        }

        public bool TryGetValue(int index, string keyName, out object? content)
        {
            content = null;
            if (index < 0 || index >= _entries.Count)
            {
                return false;
            }
            var entry = _entries[index];
            content = keyName switch
            {
                StandardTableKeyNames.ErrorSeverity => Category(entry.Severity),
                StandardTableKeyNames.BuildTool => BuildToolName,
                StandardTableKeyNames.ErrorSource => ErrorSource.Build,
                StandardTableKeyNames.DocumentName => entry.FilePath,
                StandardTableKeyNames.Line => entry.Line,
                StandardTableKeyNames.Column => entry.Column,
                StandardTableKeyNames.Text => entry.Message,
                _ => null,
            };
            return content is not null;
        }

        public void Dispose()
        {
        }

        private static __VSERRORCATEGORY Category(DamlDiagnosticSeverity severity) => severity switch
        {
            DamlDiagnosticSeverity.Error => __VSERRORCATEGORY.EC_ERROR,
            DamlDiagnosticSeverity.Warning => __VSERRORCATEGORY.EC_WARNING,
            _ => __VSERRORCATEGORY.EC_MESSAGE,
        };
    }
}
