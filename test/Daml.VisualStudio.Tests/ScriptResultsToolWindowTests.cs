using System;
using System.Threading;
using Daml.VisualStudio.ScriptResults;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

public class ScriptResultsToolWindowTests
{
    // Visual Studio wraps a pane's content into the window frame as soon as it creates the
    // window, and fails when there is none; the view only comes later.
    [Fact, Trait("Category", "Editor")]
    public void A_new_results_window_has_content_before_any_view_is_attached()
    {
        object? content = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new ScriptResultsToolWindow();
                content = window.Content;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure.ShouldBeNull();
        content.ShouldNotBeNull();
    }
}
