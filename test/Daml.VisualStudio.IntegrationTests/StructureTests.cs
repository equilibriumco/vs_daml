using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Outlining;
using Xunit;

namespace Daml.VisualStudio.IntegrationTests;

public class StructureTests
{
    [IdeFact(MinVersion = VisualStudioVersion.VS18, MaxVersion = VisualStudioVersion.VS18), Trait("Category", "Integration")]
    public async Task Collapse_to_definitions_folds_the_template_and_shows_its_name()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var view = Ide.OpenFile(Path.Combine(Ide.CopySample("single-package"), "daml", "Main.daml"));
        var outlining = Ide.ComponentModel().GetService<IOutliningManagerService>().GetOutliningManager(view);
        try
        {
            Assert.NotNull(outlining);
            bool IsTemplate(ICollapsible region) =>
                region.Extent.GetStartPoint(view.TextBuffer.CurrentSnapshot).GetContainingLine().GetText().StartsWith("template Asset", StringComparison.Ordinal);
            SnapshotSpan Whole() => new(view.TextBuffer.CurrentSnapshot, 0, view.TextBuffer.CurrentSnapshot.Length);

            // Regions are worked out in the background once the file has been quiet for a moment.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!outlining!.GetAllRegions(Whole()).Any(IsTemplate) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
            Assert.Contains(outlining.GetAllRegions(Whole()), IsTemplate);

            Ide.CollapseToDefinitions(view);

            var template = outlining.GetCollapsedRegions(Whole()).FirstOrDefault(IsTemplate);
            Assert.NotNull(template);
            Assert.Equal("template Asset …", template!.CollapsedForm);
        }
        finally
        {
            outlining?.ExpandAll(new SnapshotSpan(view.TextBuffer.CurrentSnapshot, 0, view.TextBuffer.CurrentSnapshot.Length), _ => true);
            Ide.CloseAllDocuments();
        }
    }
}
