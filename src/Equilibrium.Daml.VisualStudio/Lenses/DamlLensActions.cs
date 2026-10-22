using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.VisualStudio.Commands;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Lenses;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.Lenses;

/// <summary>The keyboard way to a lens: Ctrl+. on a declaration's line offers what its row offers.</summary>
[Export(typeof(ISuggestedActionsSourceProvider))]
[Name("Daml lens actions")]
[ContentType(DamlContentDefinition.ContentTypeName)]
internal sealed class DamlLensActionsProvider : ISuggestedActionsSourceProvider
{
    private readonly DamlLensSources _sources;
    private readonly JoinableTaskContext _joinableTaskContext;

    [ImportingConstructor]
    public DamlLensActionsProvider(DamlLensSources sources, JoinableTaskContext joinableTaskContext)
    {
        _sources = sources;
        _joinableTaskContext = joinableTaskContext;
    }

    public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer) =>
        new Source(textBuffer, _sources, _joinableTaskContext.Factory);

    private sealed class Source(ITextBuffer buffer, DamlLensSources sources, JoinableTaskFactory joinableTaskFactory) : ISuggestedActionsSource
    {
        public event EventHandler<EventArgs>? SuggestedActionsChanged
        {
            add { }
            remove { }
        }

        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken) =>
            Task.FromResult(RowOn(range) is not null);

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken) =>
            RowOn(range) is { } row
                ? [new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Any, row.Select(s => new Action(s, sources, joinableTaskFactory)).ToArray())]
                : [];

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose()
        {
        }

        // Only views that draw rows have them; other views of Daml text offer nothing.
        private IReadOnlyList<LensSegment>? RowOn(SnapshotSpan range) =>
            DamlLensRows.Existing(buffer)?.At(range.Start.GetContainingLine());
    }

    private sealed class Action(LensSegment segment, DamlLensSources sources, JoinableTaskFactory joinableTaskFactory) : ISuggestedAction
    {
        public string DisplayText => segment.AutomationName;

        public bool HasActionSets => false;

        public bool HasPreview => false;

        public string? IconAutomationText => null;

        public ImageMoniker IconMoniker => default;

        public string? InputGestureText => null;

        public Task<IEnumerable<SuggestedActionSet>?> GetActionSetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<SuggestedActionSet>?>(null);

        public Task<object?> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object?>(null);

        public void Invoke(CancellationToken cancellationToken) =>
            joinableTaskFactory.RunAsync(() => sources.InvokeAsync(segment, CancellationToken.None)).FileAndForget("daml/lenses/suggestedAction");

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Shows the results of the script at the caret: the one named on the caret's line (its
/// signature or an equation), or else the one whose body the caret is in.
/// </summary>
[Command(DamlMenuIds.CommandSetString, DamlMenuIds.ShowScriptResultsAtCaret)]
internal sealed class ShowScriptResultsAtCaretCommand : BaseCommand<ShowScriptResultsAtCaretCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var view = await VS.Documents.GetActiveDocumentViewAsync();
        var caret = view?.TextView?.Caret.Position.BufferPosition;
        var row = caret is { } position ? DamlLensRows.Existing(position.Snapshot.TextBuffer)?.ForCaret(position.GetContainingLine()) : null;
        if (row?.FirstOrDefault(s => s.Target is ScriptLens) is not { } segment)
        {
            var logger = await VS.GetMefServiceAsync<IDamlLogger>();
            var where = caret is { } at ? $"line {at.GetContainingLine().LineNumber + 1} of {view!.FilePath}" : "no Daml editor";
            logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, $"Show Script Results at Caret: no script at the caret ({where}).");
            await VS.StatusBar.ShowMessageAsync("Daml: no script at the caret. Script results appear above scripts once the language server is ready.");
            return;
        }
        var sources = await VS.GetMefServiceAsync<DamlLensSources>();
        await sources.InvokeAsync(segment, CancellationToken.None);
    }
}
