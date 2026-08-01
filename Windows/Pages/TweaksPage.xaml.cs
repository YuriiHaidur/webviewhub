using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WebViewHub.Models;

namespace WebViewHub.Windows.Pages;

public partial class TweaksPage : Page
{
    public TweaksPage() => InitializeComponent();

    /// <summary>
    /// Opens the WPF folder picker (Microsoft.Win32.OpenFolderDialog,
    /// available in .NET 8+) and writes the chosen path back to the
    /// draft via the bound <c>LocalFolderPath</c> setter. Initial
    /// directory is set to the current value when present so re-picking
    /// stays near where the user already pointed.
    /// </summary>
    private void BrowseLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ServiceConfigDraft draft) return;

        var dlg = new OpenFolderDialog
        {
            Title = "Choose local folder to serve as the virtual host root",
            Multiselect = false,
        };

        var current = draft.LocalFolderPath;
        if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current))
        {
            dlg.InitialDirectory = current;
        }

        if (dlg.ShowDialog() == true)
        {
            draft.LocalFolderPath = dlg.FolderName;
        }
    }
}
