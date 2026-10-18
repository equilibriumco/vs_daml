using System.ComponentModel.Composition;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Bindings;

/// <summary>Double-click (or Enter) on a row of the references window opens its file at its line.</summary>
[Export(typeof(ITableControlEventProcessorProvider))]
[DataSourceType(StandardTableDataSources.AnyDataSource)]
[DataSource(StandardTableDataSources.AnyDataSource)]
[ManagerType(CSharpReferencesToolWindow.ManagerIdentifier)]
[Name("Daml C# references navigation")]
[Order(Before = "default")]
internal sealed class CSharpReferencesNavigationProvider : ITableControlEventProcessorProvider
{
    private readonly JoinableTaskContext _joinableTaskContext;

    [ImportingConstructor]
    public CSharpReferencesNavigationProvider(JoinableTaskContext joinableTaskContext) => _joinableTaskContext = joinableTaskContext;

    public ITableControlEventProcessor GetAssociatedEventProcessor(IWpfTableControl tableControl) => new Navigation(_joinableTaskContext.Factory);

    private sealed class Navigation(JoinableTaskFactory joinableTaskFactory) : TableControlEventProcessorBase
    {
        public override void PreprocessNavigate(ITableEntryHandle entry, TableEntryNavigateEventArgs e)
        {
            if (!entry.TryGetValue(StandardTableKeyNames.DocumentName, out string path) || !entry.TryGetValue(StandardTableKeyNames.Line, out int line))
            {
                return;
            }
            var column = entry.TryGetValue(StandardTableKeyNames.Column, out int value) ? value : 0;
            e.Handled = true;
            joinableTaskFactory.RunAsync(async () =>
            {
                var view = await VS.Documents.OpenAsync(path);
                await joinableTaskFactory.SwitchToMainThreadAsync();
                if (view?.TextView is not { } textView)
                {
                    return;
                }
                var snapshot = textView.TextSnapshot;
                var target = snapshot.GetLineFromLineNumber(System.Math.Min(line, snapshot.LineCount - 1));
                var point = new SnapshotPoint(snapshot, System.Math.Min(target.Start.Position + column, target.End.Position));
                textView.Caret.MoveTo(point);
                textView.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0), EnsureSpanVisibleOptions.AlwaysCenter);
            }).FileAndForget("daml/csharpReferences/navigate");
        }
    }
}
