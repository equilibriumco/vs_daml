using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.TextManager.Interop;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class ClassificationTests
{
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task A_daml_file_is_coloured_with_daml_classifications()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var file = Path.Combine(RepoRoot(), "test", "fixtures", "lexer", "keywords.daml");

        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file, Guid.Empty, out _, out _, out _, out IVsTextView textView);

        var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("SComponentModel is not available.");
        var view = componentModel.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(textView);
        Assert.NotNull(view);
        var classifier = componentModel.GetService<IClassifierAggregatorService>().GetClassifier(view!.TextBuffer);
        var firstLine = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(0).Extent;

        // "module Keywords where"
        var spans = classifier.GetClassificationSpans(firstLine);
        Assert.Contains(spans, s => s.ClassificationType.IsOfType("daml.keyword") && s.Span.GetText() == "module");
        Assert.Contains(spans, s => s.ClassificationType.IsOfType("daml.type") && s.Span.GetText() == "Keywords");
        // Theme colours come through the predefined base types.
        Assert.True(spans.First(s => s.Span.GetText() == "module").ClassificationType.IsOfType("keyword"));
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Opening_a_block_comment_recolours_the_lines_below()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var file = Path.Combine(RepoRoot(), "test", "fixtures", "lexer", "keywords.daml");

        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file, Guid.Empty, out _, out _, out _, out IVsTextView textView);

        var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("SComponentModel is not available.");
        var buffer = componentModel.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(textView)!.TextBuffer;
        var classifier = componentModel.GetService<IClassifierAggregatorService>().GetClassifier(buffer);
        try
        {
            buffer.Insert(0, "{- ");

            var line3 = buffer.CurrentSnapshot.GetLineFromLineNumber(2).Extent;
            var spans = classifier.GetClassificationSpans(line3);
            Assert.NotEmpty(spans);
            Assert.All(spans, s => Assert.True(s.ClassificationType.IsOfType("daml.comment")));
        }
        finally
        {
            using var edit = buffer.CreateEdit();
            edit.Delete(0, 3);
            edit.Apply();
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(ClassificationTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daml.VisualStudio.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found above the test assembly.");
    }
}
