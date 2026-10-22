using System;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using Equilibrium.Daml.VisualStudio.ScriptResults;
using Microsoft.VisualStudio.PlatformUI;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Tests;

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

    // A tool window doesn't colour its content; a bare TextBlock is black, even in a dark theme.
    [Fact, Trait("Category", "Editor")]
    public void A_message_in_the_window_takes_its_colours_from_the_theme()
    {
        Brush? foreground = null;
        Brush? background = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var themed = new ContentControl { Content = ScriptResultsView.Message("Loading script results…") };
                themed.Resources[EnvironmentColors.ToolWindowTextBrushKey] = Brushes.Orange;
                themed.Resources[EnvironmentColors.ToolWindowBackgroundBrushKey] = Brushes.Navy;
                var message = (TextBlock)themed.Content;
                foreground = message.Foreground;
                background = message.Background;
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
        foreground.ShouldBe(Brushes.Orange);
        background.ShouldBe(Brushes.Navy);
    }
}
