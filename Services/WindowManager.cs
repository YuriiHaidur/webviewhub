using System.IO;
using System.Windows.Forms;
using WebViewHub.Helpers;
using WebViewHub.Models;
using WebViewHub.Windows;

namespace WebViewHub.Services;

/// <summary>
/// Single source of truth for what's open right now.
/// Keeps service windows alive across show/hide cycles, and provides a
/// primary hub tray icon so the user can always reach the app — even
/// when there are no service/hub windows open but global hotkeys are
/// keeping the process alive.
/// </summary>
public class WindowManager
{
    private readonly Dictionary<string, ServiceWindow> _serviceWindows = new();
    private readonly Dictionary<string, ServiceSettingsWindow> _settingsWindows = new();
    private HubWindow? _hubWindow;
    private NotifyIcon? _hubTrayIcon;
    private bool _shuttingDown;

    /// <summary>
    /// Set while <see cref="RestartService"/> tears down and recreates a
    /// window, so <see cref="MaybeShutdown"/> doesn't exit the process (or
    /// flash the fallback tray icon) in the instant between close and reopen
    /// when the restarted service happens to be the only open window.
    /// </summary>
    private bool _restarting;

    /// <summary>
    /// Opens (or focuses, if already open) the new ServiceSettingsWindow
    /// for the given service. One window per service id.
    /// </summary>
    public ServiceSettingsWindow OpenServiceSettings(ServiceConfig config, bool isNewService = false)
    {
        // For Add Service: focus any existing in-progress new-service
        // window instead of creating a parallel one. Otherwise a user
        // who clicks "+ Add service" multiple times ends up with N draft
        // windows and can accidentally save several near-identical
        // placeholder entries.
        if (isNewService)
        {
            foreach (var w in _settingsWindows.Values)
            {
                if (!w.IsNewService) continue;
                if (w.WindowState == System.Windows.WindowState.Minimized)
                    w.WindowState = System.Windows.WindowState.Normal;
                w.Activate();
                return w;
            }
        }

        if (_settingsWindows.TryGetValue(config.Id, out var existing))
        {
            if (existing.WindowState == System.Windows.WindowState.Minimized)
                existing.WindowState = System.Windows.WindowState.Normal;
            existing.Activate();
            return existing;
        }

        var window = new ServiceSettingsWindow(config, isNewService: isNewService);
        window.Closed += (_, _) =>
        {
            _settingsWindows.Remove(config.Id);
            // Keep the hub in sync — new-service cleanup or any field
            // edit that affects the tile (Name, icon path) needs a
            // refresh after the settings window is dismissed.
            _ = _hubWindow?.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await _hubWindow.RefreshAsync(); }
                catch (Exception ex) { Logger.Warn($"Hub refresh after settings close failed: {ex.Message}"); }
            }));
        };
        _settingsWindows[config.Id] = window;
        window.Show();
        return window;
    }

    /// <summary>
    /// Re-applies every side-effecting field of <paramref name="svc"/> to OS
    /// state, the open service window (if any), and saves config. Idempotent —
    /// safe to call repeatedly from the auto-apply debounce timer.
    /// </summary>
    public void ReapplyService(ServiceConfig svc)
    {
        App.ReapplyHotkey(svc);
        App.ReapplyDoubleCtrlC(svc);
        App.ReapplyProtocolHandler(svc);
        App.ReapplyShortcut(svc);
        App.ReapplyAutostart(svc);
        App.ReapplyRaycastScripts();

        if (_serviceWindows.TryGetValue(svc.Id, out var w))
        {
            try { w.ApplyConfigUpdate(svc); }
            catch (Exception ex) { Logger.Warn($"ApplyConfigUpdate('{svc.Name}') threw: {ex.Message}"); }
        }

        // Refresh the hub tile too — without this, an icon swap from the
        // picker shows up on the open service window but the hub grid
        // still displays the old image until Settings closes (which
        // triggers the post-close hub refresh).
        if (_hubWindow != null)
        {
            Logger.Debug($"ReapplyService: scheduling Hub.RefreshAsync for '{svc.Name}'.");
            _ = _hubWindow.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _hubWindow.RefreshAsync();
                    Logger.Debug($"ReapplyService: Hub.RefreshAsync done for '{svc.Name}'.");
                }
                catch (Exception ex) { Logger.Warn($"Hub refresh after ReapplyService failed: {ex.Message}"); }
            }));
        }
        else
        {
            Logger.Debug($"ReapplyService: _hubWindow is null, skipping hub refresh for '{svc.Name}'.");
        }

        _ = App.Config.SaveAsync();
    }

    public void ShowHub()
    {
        EnsureHubExists();

        if (_hubWindow!.WindowState == System.Windows.WindowState.Minimized)
            _hubWindow.WindowState = System.Windows.WindowState.Normal;

        _hubWindow.Show();
        _hubWindow.Activate();
    }

    /// <summary>
    /// Creates the Hub window (and its persistent tray icon, which
    /// HubWindow installs in its ctor) without making the window visible.
    /// Used by the <c>--start-hidden</c> autostart path so WebViewHub
    /// boots silently with just the tray icon present.
    /// </summary>
    public void EnsureHubInTray()
    {
        EnsureHubExists();
        // Don't Show() — leave Visibility=Collapsed. The tray icon is
        // already alive (HubWindow ctor calls SetupTrayIcon).
        Logger.Info("Hub created in tray-only mode (window hidden).");
    }

    private void EnsureHubExists()
    {
        if (_hubWindow != null) return;
        _hubWindow = new HubWindow();
        _hubWindow.Closed += (_, _) =>
        {
            _hubWindow = null;
            MaybeShutdown();
        };
    }

    public void OpenOrToggleService(string nameOrId, bool useHiddenStart = false)
    {
        var config = App.Config.FindByName(nameOrId) ?? App.Config.FindById(nameOrId);
        if (config == null)
        {
            ShowHub();
            return;
        }

        OpenOrToggleService(config, useHiddenStart);
    }

    /// <summary>
    /// Opens or toggles a service window. <paramref name="useHiddenStart"/>
    /// only matters for the first-creation path: when true AND
    /// <see cref="ServiceConfig.StartMinimized"/> is also true, the window
    /// inits invisibly and ends up in the tray (Windows autostart flow).
    /// Pass false for all user-initiated paths (hotkey, hub click, tray
    /// menu, post-startup pipe re-invocation) so the window shows
    /// immediately — otherwise the first hotkey press silently puts the
    /// service in the tray and the user has to press it twice.
    /// </summary>
    public void OpenOrToggleService(ServiceConfig config, bool useHiddenStart = false)
    {
        if (_serviceWindows.TryGetValue(config.Id, out var existing))
        {
            Logger.Info($"OpenOrToggle '{config.Name}' — already open, toggling.");
            if (existing.IsVisible && existing.IsActive)
            {
                existing.HideToTray();
            }
            else
            {
                existing.ShowFromTray();
            }
            return;
        }

        Logger.Info($"OpenOrToggle '{config.Name}' — creating window. " +
                    $"StartMinimized={config.StartMinimized}, MinimizeToTray={config.MinimizeToTray}, " +
                    $"CloseToTray={config.CloseToTray}, AutoStart={config.AutoStart}");
        try
        {
            var window = new ServiceWindow(config);
            window.Closed += (_, _) =>
            {
                Logger.Info($"Service window '{config.Name}' closed.");
                _serviceWindows.Remove(config.Id);
                MaybeShutdown();
            };
            _serviceWindows[config.Id] = window;

            if (useHiddenStart && config.StartMinimized)
            {
                Logger.Info($"Service window '{config.Name}' — StartMinimized: DWM-cloaked init + hide after.");
                window.RequestHideAfterInit();
                window.PrepareHiddenStart();
                Logger.Debug($"[{config.Name}] WM pre-Show (cloaked) — IsVisible={window.IsVisible} State={window.WindowState} ShowInTaskbar={window.ShowInTaskbar}");
                window.Show();
                Logger.Debug($"[{config.Name}] WM post-Show (cloaked) — IsVisible={window.IsVisible} State={window.WindowState}");
            }
            else
            {
                window.Show();
                Logger.Info($"Service window '{config.Name}' shown.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open service '{config.Name}'", ex);
            _serviceWindows.Remove(config.Id);
            throw;
        }
    }

    /// <summary>
    /// Opens the service window and navigates it to the given URL —
    /// used by the custom-protocol launch path so a deep link (e.g.
    /// "spotify:track:abc" → "https://open.spotify.com/track/abc")
    /// lands on the right page even when the window already exists.
    /// </summary>
    public void OpenServiceWithUrl(ServiceConfig config, string url)
    {
        if (config == null || string.IsNullOrEmpty(url)) return;

        if (_serviceWindows.TryGetValue(config.Id, out var existing))
        {
            existing.NavigateTo(url);
            existing.ShowFromTray();
            return;
        }

        try
        {
            var window = new ServiceWindow(config);
            window.Closed += (_, _) =>
            {
                Logger.Info($"Service window '{config.Name}' closed.");
                _serviceWindows.Remove(config.Id);
                MaybeShutdown();
            };
            _serviceWindows[config.Id] = window;
            window.NavigateTo(url);
            window.Show();
            window.Activate();
            // Show()+Activate() also hit the "no focus steal" wall when our
            // process isn't foreground (Ctrl+C+C reaches us via a low-level
            // hook, not a hotkey, so we have no foreground privilege). The
            // same AttachThreadInput trick used by ShowFromTray bypasses
            // the policy and reliably brings the brand-new window above
            // whatever app the user was just in.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            NativeMethods.ForceShowAndForeground(hwnd);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open '{config.Name}' with URL '{url}'", ex);
            _serviceWindows.Remove(config.Id);
        }
    }

    /// <summary>
    /// Opens (or refreshes) a translator service with the current clipboard
    /// content baked into the URL. Always navigates — even when the window
    /// is already open — so a second double-Ctrl+C trigger picks up newly
    /// copied text.
    /// </summary>
    public void OpenTranslator(ServiceConfig config)
    {
        if (config == null) return;

        var clipboardText = TryReadClipboardText();
        var url = TranslatorUrl.Build(config.TranslatorTargetLang, clipboardText);
        Logger.Info($"OpenTranslator '{config.Name}' — clipboard chars={clipboardText?.Length ?? 0}");

        if (_serviceWindows.TryGetValue(config.Id, out var existing))
        {
            existing.NavigateTo(url);
            existing.ShowFromTray();
            return;
        }

        try
        {
            var window = new ServiceWindow(config);
            window.Closed += (_, _) =>
            {
                Logger.Info($"Service window '{config.Name}' closed.");
                _serviceWindows.Remove(config.Id);
                MaybeShutdown();
            };
            _serviceWindows[config.Id] = window;
            window.NavigateTo(url);
            window.Show();
            window.Activate();
            // Show()+Activate() also hit the "no focus steal" wall when our
            // process isn't foreground (Ctrl+C+C reaches us via a low-level
            // hook, not a hotkey, so we have no foreground privilege). The
            // same AttachThreadInput trick used by ShowFromTray bypasses
            // the policy and reliably brings the brand-new window above
            // whatever app the user was just in.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            NativeMethods.ForceShowAndForeground(hwnd);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open translator '{config.Name}'", ex);
            _serviceWindows.Remove(config.Id);
        }
    }

    private static string? TryReadClipboardText()
    {
        try
        {
            // ContainsText() avoids spurious clipboard format probes that
            // can throw on some apps (Office) when the clipboard is busy.
            if (System.Windows.Clipboard.ContainsText())
            {
                return System.Windows.Clipboard.GetText();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Clipboard read failed: {ex.Message}");
        }
        return null;
    }

    public void CloseService(string id)
    {
        if (_serviceWindows.TryGetValue(id, out var win))
        {
            win.ForceClose();
            _serviceWindows.Remove(id);
        }
    }

    public bool IsServiceOpen(string id) => _serviceWindows.ContainsKey(id);

    /// <summary>
    /// Switches a service to a different account profile. WebView2 fixes
    /// ProfileName at controller-creation time, so the only way to change
    /// sessions is to rebuild the window — hence the restart. The config is
    /// persisted first so a crash mid-restart still reopens on the profile
    /// the user picked.
    /// </summary>
    public void SwitchProfile(string serviceId, string profileId)
    {
        var config = App.Config.FindById(serviceId);
        if (config == null)
        {
            Logger.Warn($"SwitchProfile: no config for id '{serviceId}'.");
            return;
        }

        if (config.ActiveProfileId == profileId) return;

        var target = config.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (target == null)
        {
            Logger.Warn($"SwitchProfile: '{config.Name}' has no profile '{profileId}'.");
            return;
        }

        Logger.Info($"SwitchProfile '{config.Name}' → '{target.Name}' (bucket={target.ProfileKey}).");
        config.ActiveProfileId = profileId;
        App.Config.SaveSync();

        // Only an open window needs rebuilding; a closed service will pick up
        // the new profile the next time it opens.
        if (_serviceWindows.ContainsKey(serviceId)) RestartService(serviceId);
    }

    /// <summary>
    /// Fully restarts a service: closes its window (disposing the WebView2
    /// runtime) and immediately recreates it. Login/cookies survive because
    /// the WebView2 profile on disk is left untouched — this reinitializes
    /// the runtime, not the profile. No-op if the service isn't open.
    /// </summary>
    public void RestartService(string id)
    {
        if (!_serviceWindows.ContainsKey(id)) return;

        var config = App.Config.FindById(id);
        if (config == null)
        {
            Logger.Warn($"RestartService: no config for id '{id}' — skipping.");
            return;
        }

        Logger.Info($"RestartService '{config.Name}' — closing and recreating window.");

        // CloseService fires the window's Closed handler synchronously, which
        // calls MaybeShutdown. If this was the only open window (and no
        // hotkeys keep us alive) MaybeShutdown would tear the whole process
        // down before we reopen — suppress it across the close→reopen gap.
        _restarting = true;
        try
        {
            CloseService(id);
            OpenOrToggleService(config);
        }
        finally
        {
            _restarting = false;
        }
    }

    /// <summary>
    /// Apply edits made in the hub to an already-open service window
    /// (renaming, taskbar visibility, badge mode, etc.) — without
    /// requiring the user to close and reopen it.
    /// </summary>
    public void ApplyServiceUpdate(ServiceConfig updated)
    {
        if (_serviceWindows.TryGetValue(updated.Id, out var win))
        {
            win.ApplyConfigUpdate(updated);
        }
    }

    /// <summary>
    /// Opens the service-edit dialog for one service and applies the
    /// result everywhere it matters: config save, hotkey/autostart/protocol
    /// re-registration, shortcut housekeeping, live update of any open
    /// service window, and refresh of the hub list. Returns true when
    /// the user saved.
    /// </summary>
    /// <summary>
    /// Force-close all windows and shut down the process.
    /// Called from "Quit WebViewHub" tray menu items.
    /// </summary>
    public void QuitApp()
    {
        _shuttingDown = true;

        foreach (var win in _serviceWindows.Values.ToList())
        {
            win.ForceClose();
        }
        _serviceWindows.Clear();

        _hubWindow?.ForceClose();
        _hubWindow = null;

        DisposeHubTrayIcon();

        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// If there's no UI left, exit — UNLESS some services have global
    /// hotkeys registered, in which case we stay alive in the background
    /// and surface a primary tray icon so the user can still reach us.
    /// </summary>
    private void MaybeShutdown()
    {
        if (_shuttingDown) return;
        if (_restarting) return;

        var hasUI = _hubWindow != null || _serviceWindows.Count > 0;
        if (hasUI)
        {
            // We have visible/hidden windows — no need for a primary tray icon.
            DisposeHubTrayIcon();
            return;
        }

        if (App.Hotkeys.RegisteredCount > 0 || App.DoubleCtrlC.ListenerCount > 0)
        {
            EnsureHubTrayIcon();
            return;
        }

        System.Windows.Application.Current.Shutdown();
    }

    private void EnsureHubTrayIcon()
    {
        if (_hubTrayIcon != null) return;

        var icon = TryLoadAppIcon() ?? IconHelper.GenerateLetterIcon("W");
        _hubTrayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "WebViewHub",
            Visible = true
        };

        _hubTrayIcon.Click += (s, e) =>
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
                ShowHub();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open hub", null, (_, _) => ShowHub());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var svc in App.Config.Config.Services.OrderBy(s => s.Name))
        {
            var local = svc;
            menu.Items.Add($"Open {svc.Name}", null, (_, _) => OpenOrToggleService(local));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit WebViewHub", null, (_, _) => QuitApp());
        _hubTrayIcon.ContextMenuStrip = menu;
    }

    private void DisposeHubTrayIcon()
    {
        if (_hubTrayIcon == null) return;
        _hubTrayIcon.Visible = false;
        _hubTrayIcon.Dispose();
        _hubTrayIcon = null;
    }

    private static System.Drawing.Icon? TryLoadAppIcon()
    {
        // Use any existing favicon as a stand-in for the hub icon.
        try
        {
            var first = App.Config.Config.Services.FirstOrDefault();
            if (first != null)
            {
                var p = Path.Combine(App.Paths.IconsDir, $"{first.Id}.png");
                return IconHelper.LoadIcon(p);
            }
        }
        catch { }
        return null;
    }
}
