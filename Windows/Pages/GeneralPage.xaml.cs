using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace WebViewHub.Windows.Pages;

public partial class GeneralPage : Page
{
    public GeneralPage() => InitializeComponent();

    /// <summary>
    /// Lets a ToggleSwitch live inside a CardExpander's Header without the
    /// click also collapsing/expanding the expander. We flip IsChecked
    /// ourselves and mark the event handled so the click never reaches
    /// the expander's own toggle handler.
    /// </summary>
    private void HeaderToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ToggleSwitch ts)
        {
            ts.IsChecked = !(ts.IsChecked ?? false);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Routes the icon-picker button click up to the parent
    /// ServiceSettingsWindow which knows about the underlying service
    /// (its id, plus refresh callbacks for the Hub and any open service
    /// window). Page doesn't own that state on its own.
    /// </summary>
    private void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var host = FindAncestor<ServiceSettingsWindow>(this);
        host?.OpenIconPicker();
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        DependencyObject? cur = start;
        while (cur != null && cur is not T)
        {
            cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return cur as T;
    }
}
