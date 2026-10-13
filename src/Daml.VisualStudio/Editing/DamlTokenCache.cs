using System.Collections.Generic;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Text;

namespace Daml.VisualStudio.Editing;

/// <summary>
/// The lexed lines of a buffer, shared by the features that work on tokens (bracket matching,
/// highlighting a name, smart indent, snippets, folding). Kept in step with the buffer's edits,
/// so a keystroke lexes the changed line and whatever below it the change reaches, not the file.
/// </summary>
internal sealed class DamlTokenCache
{
    private readonly object _gate = new();
    private readonly LineTokensCache _cache = new();
    private ITextSnapshot? _snapshot;

    private DamlTokenCache(ITextBuffer buffer) => buffer.Changed += OnBufferChanged;

    public static DamlTokenCache For(ITextBuffer buffer) =>
        buffer.Properties.GetOrCreateSingletonProperty(() => new DamlTokenCache(buffer));

    public IReadOnlyList<LineTokens> Get(ITextSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot == _snapshot)
            {
                return _cache.Lines;
            }
            if (_snapshot is null || snapshot.Version.VersionNumber > _snapshot.Version.VersionNumber)
            {
                // The first request, or one that got here before the buffer's Changed event:
                // start over on this snapshot and let the edits carry on from it.
                _snapshot = snapshot;
                return _cache.Reset(SnapshotText.Lines(snapshot));
            }
        }
        // An older snapshot than the one kept: lexed on its own, not kept.
        return new LineTokensCache().Reset(SnapshotText.Lines(snapshot));
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_snapshot is null || e.After.Version.VersionNumber <= _snapshot.Version.VersionNumber)
            {
                return; // nobody has asked yet, or a request already moved past this change
            }
            if (e.Before == _snapshot)
            {
                _cache.Update(SnapshotText.Lines(e.After), SnapshotText.Edit(e));
            }
            else
            {
                _cache.Reset(SnapshotText.Lines(e.After));
            }
            _snapshot = e.After;
        }
    }
}
