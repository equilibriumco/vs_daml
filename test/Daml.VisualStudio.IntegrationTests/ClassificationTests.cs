using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class ClassificationTests
{
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task A_daml_file_is_coloured_with_daml_classifications()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var view = Ide.OpenFile(Ide.CopyFixture(@"lexer\keywords.daml"));
        try
        {
            var classifier = Ide.ComponentModel().GetService<IClassifierAggregatorService>().GetClassifier(view.TextBuffer);
            var firstLine = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(0).Extent;

            // "module Keywords where"
            var spans = classifier.GetClassificationSpans(firstLine);
            Assert.Contains(spans, s => s.ClassificationType.IsOfType("daml.keyword") && s.Span.GetText() == "module");
            Assert.Contains(spans, s => s.ClassificationType.IsOfType("daml.type") && s.Span.GetText() == "Keywords");
            // Theme colours come through the predefined base types.
            Assert.True(spans.First(s => s.Span.GetText() == "module").ClassificationType.IsOfType("keyword"));
        }
        finally
        {
            Ide.CloseAllDocuments();
        }
    }

    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Opening_a_block_comment_recolours_the_lines_below()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var buffer = Ide.OpenFile(Ide.CopyFixture(@"lexer\keywords.daml")).TextBuffer;
        var classifier = Ide.ComponentModel().GetService<IClassifierAggregatorService>().GetClassifier(buffer);
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
            Ide.CloseAllDocuments();
        }
    }
}
