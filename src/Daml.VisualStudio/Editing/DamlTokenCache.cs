using System.Collections.Generic;
using System.Linq;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Text;

namespace Daml.VisualStudio.Editing;

/// <summary>
/// The lexed lines of a buffer's latest snapshot, shared by the features that work on tokens
/// (bracket matching, highlighting a name, smart indent), so moving the caret doesn't lex again.
/// </summary>
internal sealed class DamlTokenCache
{
    private readonly object _gate = new();
    private ITextSnapshot? _snapshot;
    private IReadOnlyList<LineTokens> _lines = [];

    public static DamlTokenCache For(ITextBuffer buffer) =>
        buffer.Properties.GetOrCreateSingletonProperty(() => new DamlTokenCache());

    public IReadOnlyList<LineTokens> Get(ITextSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot == _snapshot)
            {
                return _lines;
            }
        }
        var lines = LineTokens.Lex(snapshot.Lines.Select(l => l.GetText()));
        lock (_gate)
        {
            // An older snapshot asked late doesn't replace a newer one.
            if (_snapshot is null || snapshot.Version.VersionNumber > _snapshot.Version.VersionNumber)
            {
                _snapshot = snapshot;
                _lines = lines;
            }
        }
        return lines;
    }
}
