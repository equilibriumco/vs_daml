using System.Collections.Generic;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Text;

namespace Daml.VisualStudio.Editing;

/// <summary>Shares immutable token snapshots across editor features; lexing runs outside the publication lock.</summary>
internal sealed class DamlTokenCache
{
    private readonly object _gate = new();
    private LineTokensCache _cache = new();
    private ITextSnapshot? _snapshot;
    private TextContentChangedEventArgs? _change;

    private DamlTokenCache(ITextBuffer buffer) => buffer.Changed += OnBufferChanged;

    public static DamlTokenCache For(ITextBuffer buffer) =>
        buffer.Properties.GetOrCreateSingletonProperty(() => new DamlTokenCache(buffer));

    public IReadOnlyList<LineTokens> Get(ITextSnapshot snapshot)
    {
        LineTokensCache next;
        TextContentChangedEventArgs? change;
        ITextSnapshot? before;
        lock (_gate)
        {
            if (snapshot == _snapshot) { return _cache.Lines; }
            next = _cache.Fork();
            before = _snapshot;
            change = _change;
        }
        // A first lex or a comment edit can be expensive. Buffer.Changed only records the edit,
        // and never waits for a background reader to finish this work.
        if (change is not null && change.Before == before && change.After == snapshot)
        {
            next.Update(SnapshotText.Lines(snapshot), SnapshotText.Edit(change));
        }
        else
        {
            next.Reset(SnapshotText.Lines(snapshot));
        }
        lock (_gate)
        {
            if (snapshot == _snapshot) { return _cache.Lines; }
            if (_snapshot is null || snapshot.Version.VersionNumber > _snapshot.Version.VersionNumber)
            {
                _snapshot = snapshot;
                _cache = next;
                if (_change?.After.Version.VersionNumber <= snapshot.Version.VersionNumber) { _change = null; }
            }
        }
        return next.Lines;
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_snapshot is not null && e.After.Version.VersionNumber > _snapshot.Version.VersionNumber)
            {
                _change = e;
            }
        }
    }
}
