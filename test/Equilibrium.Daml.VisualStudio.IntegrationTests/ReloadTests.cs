using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.IntegrationTests;

public class ReloadTests
{
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Reloading_unchanged_text_keeps_colouring_and_token_caches_usable()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        const string text = "module Scratch where\n\nvalue = 42\n";
        var (_, view) = Ide.OpenScratch(text);
        var buffer = view.TextBuffer;
        try
        {
            var classifier = Ide.ComponentModel().GetService<IClassifierAggregatorService>().GetClassifier(buffer);
            Assert.NotEmpty(classifier.GetClassificationSpans(buffer.CurrentSnapshot.GetLineFromLineNumber(0).Extent));
            var documents = Ide.ComponentModel().GetService<ITextDocumentFactoryService>();
            Assert.True(documents.TryGetTextDocument(buffer, out var document));
            TextContentChangedEventArgs? reloaded = null;
            buffer.Changed += (_, change) => reloaded = change;

            document.Reload(EditOptions.DefaultMinimalChange);

            Assert.NotNull(reloaded);
            Assert.Empty(reloaded!.Changes);
            Assert.Equal(text, buffer.CurrentSnapshot.GetText());
            // VS guards buffer event handlers, so an exception there would otherwise be
            // swallowed and this test could pass while the editor shows an error dialog.
            var extension = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Equilibrium.Daml.VisualStudio");
            var snapshots = extension.GetType("Equilibrium.Daml.VisualStudio.Editing.SnapshotText", throwOnError: true)!;
            snapshots.GetMethod("Edit", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [reloaded]);
            Assert.Contains(classifier.GetClassificationSpans(buffer.CurrentSnapshot.GetLineFromLineNumber(0).Extent),
                span => span.ClassificationType.IsOfType("daml.keyword") && span.Span.GetText() == "module");
            buffer.Insert(buffer.CurrentSnapshot.Length, "\nother = value\n");
            Assert.Contains(classifier.GetClassificationSpans(buffer.CurrentSnapshot.GetLineFromLineNumber(4).Extent),
                span => span.ClassificationType.IsOfType("daml.identifier") && span.Span.GetText() == "other");
        }
        finally { Ide.CloseAllDocuments(); }
    }
}
