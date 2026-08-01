using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using WebViewHub.Models;

namespace WebViewHub.Windows.Pages;

public partial class ShortcutPage : Page
{
    public ShortcutPage() => InitializeComponent();

    /// <summary>
    /// Captures Ctrl/Alt/Shift/Win + key combos pressed while focus is on
    /// the hotkey TextBox, formats them, and writes back to Draft.Hotkey.
    /// </summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Modifier keys alone don't form a usable hotkey — wait for the
        // primary key to actually finish the combo.
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt  || key == Key.RightAlt  ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
            return;

        // Esc — just unfocus, leave existing combo alone.
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) parts.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Alt)     == ModifierKeys.Alt)     parts.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift)   parts.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows) parts.Add("Win");
        parts.Add(key.ToString());
        var combo = string.Join("+", parts);

        if (DataContext is ServiceConfigDraft draft)
            draft.Hotkey = combo;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServiceConfigDraft draft)
            draft.Hotkey = "";
    }
}
