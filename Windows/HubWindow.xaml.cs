using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WebViewHub.Helpers;
using WebViewHub.Models;
using WebViewHub.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace WebViewHub.Windows;

public partial class HubWindow : FluentWindow
{
    /// <summary>
    /// Stable identity for the hub. Different from any service window
    /// (which uses "{AumidPrefix}.{id}") so the hub gets its own taskbar
    /// group and doesn't inherit a pinned service's icon. Matches
    /// ShortcutManager.HubAumid so a pinned hub shortcut groups with the
    /// running window.
    /// </summary>
    private static readonly string HubAppId = ShortcutManager.HubAumid;

    /// <summary>
    /// Target card width used to compute the responsive column count.
    /// Cards stretch to fill their column, so this is the minimum visual
    /// width before a row breaks to fewer columns.
    /// </summary>
    private const double TargetCardWidth = 240;

    private System.Windows.Controls.Primitives.UniformGrid? _itemsPanel;
    private TaskbarIcon? _trayIcon;

    /// <summary>True after the user picked "Quit WebViewHub" from the tray
    /// menu, so the close handler skips its hide-to-tray default.</summary>
    private bool _allowRealClose;

    public HubWindow()
    {
        InitializeComponent();

        // Temporary hub branding — using the WPF-UI library's own logo
        // (the four blue bars) bundled as Resources\AppIcon.png. Replace
        // when we have a proper WebViewHub mark.
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Resources/AppIcon.png"));

        Title = "WebViewHub";
        HubTitleBar.Title = "WebViewHub";
        BrandTitle.Text = "WebViewHub";

        // Re-apply WPF-UI theme + Mica when the Windows system theme
        // changes at runtime (e.g. dark mode toggled in Settings).
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await RefreshAsync();
        SizeChanged += (_, _) => UpdateColumnCount();
        DpiChanged += OnDpiChanged;
        Closing += OnClosing;
        Closed += (_, _) => DisposeTrayIcon();
        // Theme change → tile gradients are baked Light/Dark variants of
        // each icon's dominant colour (see IconHelper.CreatePastelGradient),
        // so refresh the list to re-build them with the new theme target.
        ApplicationThemeManager.Changed += OnAppThemeChanged;
        Closed += (_, _) => ApplicationThemeManager.Changed -= OnAppThemeChanged;
        UpdateFooter();
        SetupTrayIcon();
    }

    private async void OnAppThemeChanged(ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { Logger.Warn($"[Hub] OnAppThemeChanged refresh failed: {ex.Message}"); }
    }

    /// <summary>
    /// Compresses a service URL into a host-only string for the tile
    /// subtitle ("https://app.productive.io/53016-foo" → "app.productive.io").
    /// Falls back to the raw URL on any parse error so the user always
    /// sees *something*. Full URL stays in the tile's ToolTip.
    /// </summary>
    private static string ShortenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            return string.IsNullOrEmpty(host) ? url : host;
        }
        return url;
    }

    /// <summary>
    /// X button defaults to hide-to-tray so the hub keeps a persistent
    /// tray presence (matches per-service window behavior). Real exit
    /// happens via the tray menu's "Quit WebViewHub" item.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        LogCurrentSize("Closing");
        if (_allowRealClose) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Captures the window's natural size for tuning the default
    /// Width/Height in XAML. <see cref="RestoreBounds"/> is used so a
    /// maximized window still reports the size it was at before maximize.
    /// </summary>
    private void LogCurrentSize(string reason)
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        Logger.Info($"[Hub] size on {reason}: Width={bounds.Width:F0} Height={bounds.Height:F0} (Left={bounds.Left:F0} Top={bounds.Top:F0})");
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            // Use Icon (System.Drawing.Icon) directly, NOT IconSource.
            // H.NotifyIcon's IconSource→Icon conversion goes through
            // Application.GetResourceStream(BitmapImage.UriSource), which
            // NREs on stream-backed BitmapImages (the path we get from
            // pack-URI .ico loads on net8). Icon is the underlying
            // property anyway — IconSource just feeds it.
            Icon = LoadAppTrayIcon(),
            ToolTipText = "WebViewHub",
            ContextMenu = BuildTrayMenu(),
            // RightClick only — left click handled by TrayLeftMouseUp below.
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };

        _trayIcon.TrayLeftMouseUp += (_, _) => ToggleVisibility();

        // Rebuild the context menu just before each right-click open so
        // the Show/Hide label and per-service entries reflect current
        // state. TrayRightMouseDown fires *before* the library calls
        // ContextMenu.IsOpen = true, so replacement is in time.
        _trayIcon.TrayRightMouseDown += (_, _) =>
        {
            _trayIcon.ContextMenu = BuildTrayMenu();
        };

        // Ensures the underlying HWND + Shell_NotifyIcon registration
        // happens immediately rather than deferred to first render.
        _trayIcon.ForceCreate();
    }

    /// <summary>
    /// Loads the bundled AppIcon.ico via the pack URI as a
    /// System.Drawing.Icon. H.NotifyIcon's TaskbarIcon takes this type
    /// for its <c>Icon</c> property and feeds it straight into
    /// Shell_NotifyIcon (skips the IconSource→ToStream conversion that
    /// crashes on stream-backed BitmapImages).
    /// </summary>
    private static System.Drawing.Icon? LoadAppTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/AppIcon.ico");
            var sri = System.Windows.Application.GetResourceStream(uri);
            if (sri?.Stream == null) return null;
            using var s = sri.Stream;
            return new System.Drawing.Icon(s);
        }
        catch
        {
            return null;
        }
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = TrayContextMenu.Create();
        var showLabel = (IsVisible && WindowState != WindowState.Minimized) ? "Hide" : "Show hub";
        menu.AddItem(showLabel,
            (IsVisible && WindowState != WindowState.Minimized) ? SymbolRegular.EyeOff24 : SymbolRegular.Eye24,
            (_, _) => ToggleVisibility());

        // Per-service quick-open entries. Sorted by display name so the menu
        // stays readable even when the hub itself shows them in insertion
        // order on the tile grid.
        var services = App.Config.Config.Services.OrderBy(s => s.Name).ToList();
        if (services.Count > 0)
        {
            menu.AddSeparator();
            foreach (var svc in services)
            {
                var local = svc;
                menu.AddItem($"Open {local.Name}", SymbolRegular.Open24,
                    (_, _) => App.Windows.OpenOrToggleService(local));
            }
        }

        menu.AddSeparator();
        menu.AddItem("Hub settings…", SymbolRegular.Settings24,
            (_, _) => OpenHubSettings());
        menu.AddItem("Quit WebViewHub", SymbolRegular.Power24,
            (_, _) => { _allowRealClose = true; App.Windows.QuitApp(); });
        return menu;
    }

    private void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive)
        {
            Hide();
            return;
        }

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null) return;
        // H.NotifyIcon: Dispose() unregisters from Shell_NotifyIcon and
        // releases the HICON the library created from IconSource.
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    /// <summary>
    /// Closes the window for real (bypassing the hide-to-tray default).
    /// Call from WindowManager.QuitApp so shutdown isn't blocked.
    /// </summary>
    public void ForceClose()
    {
        _allowRealClose = true;
        Close();
    }

    /// <summary>
    /// Captures the actual UniformGrid instance the moment ItemsControl
    /// materializes its ItemsPanel — the panel is templated so it can't
    /// be referenced by x:Name from code-behind directly.
    /// </summary>
    private void ItemsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        _itemsPanel = sender as System.Windows.Controls.Primitives.UniformGrid;
        UpdateColumnCount();
    }

    private void UpdateColumnCount()
    {
        if (_itemsPanel == null || ServicesScrollViewer == null) return;

        // Padding from the ScrollViewer is 24 on each side; viewport is
        // already minus that. Use ViewportWidth so the scrollbar doesn't
        // throw the math off when it appears.
        var available = ServicesScrollViewer.ViewportWidth;
        if (available <= 0) available = ServicesScrollViewer.ActualWidth;
        if (available <= 0) return;

        var cols = Math.Max(1, (int)(available / TargetCardWidth));
        if (_itemsPanel.Columns != cols) _itemsPanel.Columns = cols;
    }

    /// <summary>
    /// Renders the footer. When the app detects it was relocated since the
    /// previous run, we surface a one-shot green confirmation so the user
    /// knows the Start-menu shortcuts and Raycast scripts have been healed
    /// to point at the new exe path. Otherwise we just show the data dir.
    /// </summary>
    private void UpdateFooter()
    {
        if (App.WasRelocated)
        {
            FooterText.Text = "✓ Detected new install location — Start-menu shortcuts, autostart entries, " +
                              "and Raycast scripts have been updated to point here." +
                              $"    Data: {App.Paths.DataDir}";
            FooterText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x65, 0x34));
            return;
        }

        FooterText.Text = $"Data: {App.Paths.DataDir}" +
                          (App.Paths.IsPortable ? "  (portable)" : "  (AppData fallback)");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowAppProperties(hwnd, HubAppId, null, null, null);
        }
        catch { /* identity is non-critical */ }
    }

    private void OnDpiChanged(object? sender, System.Windows.DpiChangedEventArgs e)
    {
        Logger.Debug($"[Hub] DPI changed {e.OldDpi.PixelsPerInchX} → {e.NewDpi.PixelsPerInchX}");
        // H.NotifyIcon's TaskbarIcon listens for WM_DPICHANGED internally
        // and re-renders the HICON from IconSource at the new slot size,
        // so we don't need to manually reload here. Left as a hook in case
        // we add other DPI-dependent state later.
    }

    public async Task RefreshAsync()
    {
        var items = new List<HubServiceItem>();
        // Iterate in config-list order = insertion order. Newly added
        // services land at the end via Services.Add(...) in the Save
        // flow, so the hub shows them in the order the user created them.
        foreach (var svc in App.Config.Config.Services)
        {
            var iconPath = await App.Favicon.GetIconAsync(svc.Id, svc.Url, svc.Name);
            ImageSource icon = IconHelper.LoadWpfImage(iconPath)
                              ?? IconHelper.GenerateLetterImage(svc.Name);
            items.Add(new HubServiceItem
            {
                Id = svc.Id,
                Name = svc.Name,
                Url = svc.Url,
                UrlDisplay = ShortenUrl(svc.Url),
                Icon = icon,
                BackgroundGradient = IconHelper.BuildIconGradient(iconPath),
                HotkeyText = string.IsNullOrEmpty(svc.Hotkey) ? "" : svc.Hotkey,
                HotkeyVisibility = string.IsNullOrEmpty(svc.Hotkey) ? Visibility.Collapsed : Visibility.Visible,
                AutoStartVisibility = svc.AutoStart ? Visibility.Visible : Visibility.Collapsed,
                ShortcutVisibility = svc.HasShortcut ? Visibility.Visible : Visibility.Collapsed,
                BadgeVisibility = svc.UnreadBadge != UnreadBadgeMode.Off ? Visibility.Visible : Visibility.Collapsed,
                TranslatorVisibility = svc.IsTranslator ? Visibility.Visible : Visibility.Collapsed,
                // Compact label — "translator" only. Target language and
                // double-Ctrl-C details lived in the previous "translator
                // → ru (Ctrl+C, Ctrl+C)" string but they bloated the tag
                // row and pushed cards into 2-row tag layout. The fields
                // still exist in service settings if the user needs them.
                TranslatorText = svc.IsTranslator ? "translator" : "",
                ProtocolVisibility = svc.RegisterProtocol && !string.IsNullOrEmpty(svc.ProtocolScheme)
                    ? Visibility.Visible : Visibility.Collapsed,
                ProtocolText = !string.IsNullOrEmpty(svc.ProtocolScheme) ? $"{svc.ProtocolScheme}://" : "",
            });
        }

        // Force a full rebind by clearing ItemsSource first — WPF compares
        // collection references before deciding to rebuild containers, and
        // some recycler paths reuse the old Image controls (which then keep
        // displaying the previously-bound BitmapImage even though the new
        // list has a fresh one).
        ServicesList.ItemsSource = null;
        ServicesList.ItemsSource = items;
        Logger.Debug($"Hub RefreshAsync — {items.Count} items rebound.");
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HubSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenHubSettings();
    }

    /// <summary>
    /// Opens (or focuses) the Hub-settings dialog. Single-instance — a
    /// second click while it's already open just activates the existing
    /// window instead of stacking another one on top.
    /// </summary>
    public static void OpenHubSettings()
    {
        var existing = System.Windows.Application.Current.Windows
            .OfType<HubSettingsWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }
        var win = new HubSettingsWindow(App.Config.Config.HubSettings);
        // Owner = the hub window if it's currently visible, otherwise no
        // owner — works fine in tray-only mode where Hub itself is hidden.
        var hubWin = System.Windows.Application.Current.Windows.OfType<HubWindow>().FirstOrDefault();
        if (hubWin != null && hubWin.IsVisible) win.Owner = hubWin;
        win.Show();
    }

    private void AddService_Click(object sender, RoutedEventArgs e)
    {
        // Create the service in memory only — don't add to config yet.
        // ServiceSettingsWindow.Save commits it to config + saves; Cancel
        // discards it. Hub is refreshed via WindowManager's Closed handler.
        var svc = new ServiceConfig
        {
            Name = "New service",
            Url = "https://example.com",
        };
        App.Windows.OpenServiceSettings(svc, isNewService: true);
    }

    private void EditService_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (id == null) return;

        var svc = App.Config.FindById(id);
        if (svc == null) return;

        App.Windows.OpenServiceSettings(svc);
    }

    /// <summary>
    /// Damps the wheel-scroll delta. Default WPF wheel scrolls 3 lines per
    /// notch which feels jumpy on a tile grid; this scales the delta down
    /// so each notch nudges the content ~half a card height.
    /// </summary>
    private void ServicesScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.4);
            e.Handled = true;
        }
    }

    private async void DeleteService_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (id == null) return;

        var existing = App.Config.FindById(id);
        if (existing == null) return;

        var dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
        {
            Title = "Delete service",
            Content = $"Delete \"{existing.Name}\"?\n\nThis removes the service from the hub. " +
                      "The local profile folder (cookies, login state) is kept on disk in case " +
                      "you want to add it back.",
            DialogMaxWidth = 460,
            PrimaryButtonText = "Delete",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Cancel",
        };

        var result = await dialog.ShowAsync();
        if (result != Wpf.Ui.Controls.ContentDialogResult.Primary) return;

        App.Windows.CloseService(existing.Id);
        App.Hotkeys.Remove(existing.Id);
        App.RemoveDoubleCtrlC(existing.Id);
        App.RemoveProtocolHandler(existing);
        AutostartManager.Remove(existing.Id);
        ShortcutManager.Remove(existing.Name);
        RaycastScriptManager.Remove(existing.Name);

        App.Config.Config.Services.Remove(existing);
        await App.Config.SaveAsync();
        App.ReapplyRaycastScripts();
        await RefreshAsync();
    }

    private void OpenService_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (id == null) return;

        var svc = App.Config.FindById(id);
        if (svc == null) return;

        App.Windows.OpenOrToggleService(svc);
    }

    /// <summary>
    /// Whole-card click handler — opens (or toggles) the service the same
    /// way the explicit "Open" button used to. Matches Microsoft "Windows
    /// App" UX: tile = primary action, ... button = secondary menu.
    /// TileMenu_Click already sets e.Handled, so a click on the ...
    /// button bubbles up here as Handled and we skip the open.
    /// </summary>
    private void ServiceCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        var id = (sender as FrameworkElement)?.Tag as string;
        if (id == null) return;

        var svc = App.Config.FindById(id);
        if (svc == null) return;

        App.Windows.OpenOrToggleService(svc);
        e.Handled = true;
    }

    /// <summary>
    /// Opens the service URL in the user's default system browser via
    /// the shell — bypasses our embedded WebView2 entirely. Useful when
    /// the user wants real-browser behavior (extensions, password
    /// manager, etc.) for a one-off visit.
    /// </summary>
    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (id == null) return;

        var svc = App.Config.FindById(id);
        if (svc == null || string.IsNullOrWhiteSpace(svc.Url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = svc.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"OpenInBrowser failed for '{svc.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the tile's context menu next to the More button. The menu
    /// is declared on the outer tile Border, so we walk the visual tree
    /// up from the button until we hit something with a ContextMenu.
    /// </summary>
    private void TileMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement btn) return;

        DependencyObject? current = btn;
        while (current is not null)
        {
            if (current is FrameworkElement fe && fe.ContextMenu is { } menu)
            {
                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
                return;
            }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
    }

    /// <summary>
    /// Applies hotkey/autostart/shortcut state for one service and refreshes
    /// the Raycast scripts directory. If a hotkey fails to register
    /// (collision with another app), warns the user but keeps the rest of
    /// the changes.
    /// </summary>
    private async Task ApplyServiceIntegrations(ServiceConfig svc, string? oldName)
    {
        // Make sure the favicon is on disk: shortcuts use it as their .ico,
        // and Raycast scripts copy it into the watched folder for icons.
        await App.Favicon.GetIconAsync(svc.Id, svc.Url, svc.Name);

        var hotkeyOk = App.ReapplyHotkey(svc);
        App.ReapplyDoubleCtrlC(svc);
        App.ReapplyProtocolHandler(svc);
        App.ReapplyAutostart(svc);
        App.ReapplyShortcut(svc);
        App.ReapplyRaycastScripts();

        if (!hotkeyOk && !string.IsNullOrWhiteSpace(svc.Hotkey))
        {
            MessageBox.Show(
                $"Couldn't register hotkey \"{svc.Hotkey}\" — it's likely already used by another app.\n" +
                "The service is saved; pick a different combination in Edit when you're ready.",
                "Hotkey conflict",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private class HubServiceItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>Short host-only form of <see cref="Url"/> for the
        /// subtitle line under the tile name. Full URL stays in
        /// <see cref="Url"/> for the ToolTip and copy-link actions.</summary>
        public string UrlDisplay { get; set; } = "";
        public ImageSource? Icon { get; set; }
        /// <summary>Pastel vertical gradient derived from the dominant
        /// colour of <see cref="Icon"/>. Bound to the tile header so each
        /// service tile gets its own brand-tinted backdrop, Windows-App
        /// style.</summary>
        public System.Windows.Media.Brush? BackgroundGradient { get; set; }

        public string HotkeyText { get; set; } = "";
        public Visibility HotkeyVisibility { get; set; }
        public Visibility AutoStartVisibility { get; set; }
        public Visibility ShortcutVisibility { get; set; }
        public Visibility BadgeVisibility { get; set; }
        public string TranslatorText { get; set; } = "";
        public Visibility TranslatorVisibility { get; set; }
        public string ProtocolText { get; set; } = "";
        public Visibility ProtocolVisibility { get; set; }
    }
}
