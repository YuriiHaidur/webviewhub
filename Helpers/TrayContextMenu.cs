using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace WebViewHub.Helpers;

/// <summary>
/// Builds Fluent-styled WPF <see cref="ContextMenu"/>s for the H.NotifyIcon
/// <c>TaskbarIcon</c> control. The menu attaches to <c>TaskbarIcon.ContextMenu</c>
/// and the library handles open/dismiss natively — no SetWinEventHook
/// foreground tracking needed (that hack was the legacy workaround for
/// WinForms NotifyIcon, removed when we migrated to H.NotifyIcon).
/// </summary>
public static class TrayContextMenu
{
    public static ContextMenu Create()
    {
        // Inherits Fluent (acrylic/Mica) styling via the App.xaml
        // ControlsDictionary. Placement gets driven by H.NotifyIcon
        // based on the cursor position at right-click time.
        return new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };
    }

    public static MenuItem AddItem(this ContextMenu menu, string header, SymbolRegular icon, RoutedEventHandler onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new SymbolIcon { Symbol = icon },
        };
        item.Click += onClick;
        menu.Items.Add(item);
        return item;
    }

    public static void AddSeparator(this ContextMenu menu)
    {
        menu.Items.Add(new Separator());
    }

    /// <summary>
    /// Adds a parent item that only opens a submenu — it carries no click
    /// action of its own. Populate the returned item with the MenuItem
    /// overloads below.
    /// </summary>
    public static MenuItem AddSubmenu(this ContextMenu menu, string header, SymbolRegular icon)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new SymbolIcon { Symbol = icon },
        };
        menu.Items.Add(item);
        return item;
    }

    /// <summary>
    /// Submenu entry. <paramref name="icon"/> is nullable because most rows in
    /// a selection list want an empty icon column — only the selected one
    /// carries a checkmark.
    /// </summary>
    public static MenuItem AddItem(this MenuItem parent, string header, SymbolRegular? icon, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        if (icon.HasValue) item.Icon = new SymbolIcon { Symbol = icon.Value };
        item.Click += onClick;
        parent.Items.Add(item);
        return item;
    }

    public static void AddSeparator(this MenuItem parent)
    {
        parent.Items.Add(new Separator());
    }
}
