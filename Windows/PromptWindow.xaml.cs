using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WebViewHub.Windows;

/// <summary>
/// Small modal prompt (text input or plain confirmation) for use from
/// <see cref="ServiceWindow"/>.
///
/// Why a real Window instead of the ContentDialog used everywhere else:
/// ServiceWindow hosts a WebView2, which is an <c>HwndHost</c> — a native
/// child window. WPF cannot render its own content above an HwndHost
/// ("airspace"), so a ContentDialog overlaying the WebView2 goes invisible
/// while still capturing WPF input: the page keeps working, the title bar
/// goes dead, and the user sees nothing. A top-level window has its own
/// HWND and is immune to that.
/// </summary>
public partial class PromptWindow : FluentWindow
{
    /// <summary>Text the user entered. Empty for confirmation-only prompts.</summary>
    public string InputText => InputBox.Text.Trim();

    private PromptWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
    }

    /// <summary>
    /// Asks for a line of text. Returns the trimmed input, or null if the
    /// user cancelled. Blank input is treated as a cancel by the caller.
    /// </summary>
    public static string? AskForText(
        Window owner,
        string title,
        string message,
        string primaryButtonText,
        string initialValue = "")
    {
        var w = new PromptWindow { Owner = owner, Title = title };
        w.MessageText.Text = message;
        w.PrimaryButton.Content = primaryButtonText;
        w.InputBox.Text = initialValue;

        w.Loaded += (_, _) =>
        {
            w.InputBox.Focus();
            w.InputBox.SelectAll();
        };

        return w.ShowDialog() == true ? w.InputText : null;
    }

    /// <summary>
    /// Yes/no confirmation with no input field. <paramref name="danger"/>
    /// paints the primary button red for destructive actions.
    /// </summary>
    public static bool Confirm(
        Window owner,
        string title,
        string message,
        string primaryButtonText,
        bool danger = false)
    {
        var w = new PromptWindow { Owner = owner, Title = title };
        w.MessageText.Text = message;
        w.PrimaryButton.Content = primaryButtonText;
        w.InputBox.Visibility = Visibility.Collapsed;
        if (danger) w.PrimaryButton.Appearance = ControlAppearance.Danger;

        w.Loaded += (_, _) => w.PrimaryButton.Focus();

        return w.ShowDialog() == true;
    }

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Enter commits — a one-field prompt shouldn't need a mouse trip.
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        DialogResult = true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
