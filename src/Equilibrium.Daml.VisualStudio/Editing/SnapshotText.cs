using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Text;

namespace Equilibrium.Daml.VisualStudio.Editing;

/// <summary>Editor snapshots and their changes in the line terms the syntax caches work in.</summary>
internal static class SnapshotText
{
    /// <summary>The lines of <paramref name="snapshot"/>, read on demand.</summary>
    public static ILineSource Lines(ITextSnapshot snapshot) => new SnapshotLines(snapshot);

    /// <summary>
    /// All of one version's changes as a single line edit: from the first changed line to the
    /// last, with the line count difference for matching up the lines below.
    /// </summary>
    public static LineEdit Edit(TextContentChangedEventArgs e)
    {
        var changes = e.Changes;
        var firstLine = e.Before.GetLineNumberFromPosition(changes[0].OldPosition);
        var lastLine = e.After.GetLineNumberFromPosition(changes[changes.Count - 1].NewEnd);
        return new LineEdit(firstLine, lastLine, e.After.LineCount - e.Before.LineCount);
    }

    private sealed class SnapshotLines(ITextSnapshot snapshot) : ILineSource
    {
        public int LineCount => snapshot.LineCount;

        public string GetLineText(int line) => snapshot.GetLineFromLineNumber(line).GetText();
    }
}
