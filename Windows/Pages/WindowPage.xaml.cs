using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace WebViewHub.Windows.Pages;

public partial class WindowPage : Page
{
    public WindowPage() => InitializeComponent();

    /// <summary>
    /// Same trick as on GeneralPage / IntegrationPage — let a ToggleSwitch
    /// live inside a CardExpander Header without a click also collapsing
    /// or expanding the expander.
    /// </summary>
    private void HeaderToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ToggleSwitch ts)
        {
            ts.IsChecked = !(ts.IsChecked ?? false);
            e.Handled = true;
        }
    }
}
