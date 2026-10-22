using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Equilibrium.Daml.VisualStudio.Bindings;

/// <summary>Asks which of several Daml sources to go to; nothing is picked for the user.</summary>
internal static class DamlSourceChooser
{
    /// <summary>The source the user chose, or null when they cancelled.</summary>
    public static DamlNavigationTarget? Choose(string message, IReadOnlyList<DamlNavigationTarget> targets)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var list = new ListBox
        {
            ItemsSource = targets.Select(t => $"{DamlNavigationPlanner.Name(t.Symbol)}  —  {t.Source.ManifestPath}").ToList(),
            MinHeight = 120,
            MaxHeight = 360,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var go = new Button { Content = "Go", IsDefault = true, IsEnabled = false, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(go);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(12), MinWidth = 420, MaxWidth = 720 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(list);
        content.Children.Add(buttons);
        var window = new DialogWindow
        {
            Title = "Go to Daml Definition",
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            HasMinimizeButton = false,
            HasMaximizeButton = false,
        };
        list.SelectionChanged += (_, _) => go.IsEnabled = list.SelectedIndex >= 0;
        list.MouseDoubleClick += (_, _) => window.DialogResult = list.SelectedIndex >= 0;
        go.Click += (_, _) => window.DialogResult = true;
        return window.ShowModal() == true && list.SelectedIndex >= 0 ? targets[list.SelectedIndex] : null;
    }
}
