using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.ScriptResults;
using Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Bindings;

/// <summary>Follows the caret of every C# editor, for the Daml commands that depend on it.</summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("CSharp")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class DamlCaretBindingsListener : IWpfTextViewCreationListener
{
    private readonly DamlBindingService _service;
    private readonly JoinableTaskContext _joinableTaskContext;

    [ImportingConstructor]
    public DamlCaretBindingsListener(DamlBindingService service, JoinableTaskContext joinableTaskContext)
    {
        _service = service;
        _joinableTaskContext = joinableTaskContext;
    }

    public void TextViewCreated(IWpfTextView textView) =>
        textView.Properties.GetOrCreateSingletonProperty(() => new DamlCaretBindings(textView, _service, _joinableTaskContext.Factory));
}

/// <summary>
/// Whether the name at a C# editor's caret stands for Daml, looked up a moment after the caret
/// stops moving, so a context menu can tell at once whether to offer going there. The answer
/// always belongs to a caret position; the command itself looks again before it goes.
/// </summary>
internal sealed class DamlCaretBindings : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    private readonly ITextView _view;
    private readonly DamlBindingService _service;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Debouncer _debouncer = new(Quiet, TimeProvider.System);
    private volatile Answer? _answer;

    public DamlCaretBindings(ITextView view, DamlBindingService service, JoinableTaskFactory joinableTaskFactory)
    {
        _view = view;
        _service = service;
        _joinableTaskFactory = joinableTaskFactory;
        _view.Caret.PositionChanged += OnCaretMoved;
        _view.Closed += OnClosed;
        LookAt(_view.Caret.Position.BufferPosition);
    }

    /// <summary>The caret tracking of <paramref name="view"/>, if it is a C# editor.</summary>
    public static DamlCaretBindings? Of(ITextView view) =>
        view.Properties.TryGetProperty(typeof(DamlCaretBindings), out DamlCaretBindings bindings) ? bindings : null;

    /// <summary>Whether the name at the caret, as it is now, was found to stand for Daml.</summary>
    public bool AtCaret
    {
        get
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var caret = _view.Caret.Position.BufferPosition;
            return _answer is { Found: { } found } answer && answer.Snapshot == caret.Snapshot
                && found.Span.Start <= caret.Position && caret.Position <= found.Span.End;
        }
    }

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) => LookAt(e.NewPosition.BufferPosition);

    /// <summary>Stops following the caret.</summary>
    public void Dispose()
    {
        _view.Caret.PositionChanged -= OnCaretMoved;
        _view.Closed -= OnClosed;
        _debouncer.Dispose();
    }

    private void OnClosed(object sender, EventArgs e) => Dispose();

    private void LookAt(SnapshotPoint point) =>
        _joinableTaskFactory.RunAsync(async () =>
        {
            if (!await _debouncer.WaitAsync())
            {
                return;
            }
            await TaskScheduler.Default;
            var document = point.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
            var found = document is null ? null : await _service.FindAsync(document, point.Position, CancellationToken.None);
            _answer = new Answer(point.Snapshot, found?.Found);
        }).FileAndForget("daml/bindings/caret");

    private sealed record Answer(ITextSnapshot Snapshot, BindingsAtPosition? Found);
}
