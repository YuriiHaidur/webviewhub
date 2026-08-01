using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using WebViewHub.Models;
using WebViewHub.Services;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WebViewHub.Windows;

public partial class ServiceSettingsWindow : FluentWindow, INotifyPropertyChanged
{
    private readonly ServiceConfig _config;
    private readonly ServiceConfigDraft _draft;
    private readonly bool _isNewService;

    /// <summary>True after Save / Cancel / discard-on-X. Used by OnClosing to
    /// skip the dirty-check prompt because the user already made the call.</summary>
    private bool _explicitCloseDecision;


    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Exposed so WindowManager can find an existing in-progress
    /// "Add service" window before opening another.</summary>
    public bool IsNewService => _isNewService;

    /// <summary>
    /// Save button enabled when:
    ///  - Draft is dirty (field edits OR a pending icon from the picker)
    ///  - AND Name + URL aren't empty
    ///  - AND Name doesn't collide with another existing service.
    /// </summary>
    public bool CanSave =>
        _draft.IsDirty
        && !string.IsNullOrWhiteSpace(_draft.Name)
        && !string.IsNullOrWhiteSpace(_draft.Url)
        && _draft.NameConflictHint == null;

    /// <summary>Tooltip shown on the (disabled) Save button explaining
    /// why it's disabled. Empty when Save is enabled or unset.</summary>
    public string? SaveBlockedReason
    {
        get
        {
            if (!_draft.IsDirty) return null;
            if (string.IsNullOrWhiteSpace(_draft.Name)) return "Name is required.";
            if (string.IsNullOrWhiteSpace(_draft.Url)) return "URL is required.";
            return _draft.NameConflictHint;
        }
    }

    public ServiceSettingsWindow(ServiceConfig config, bool isNewService = false)
    {
        // Initialize _draft + flags BEFORE InitializeComponent so the
        // CanSave binding (IsEnabled on Save button) reads a valid draft
        // when bindings get evaluated. Otherwise _draft is null at parse
        // time, CanSave throws, binding falls back to default → Save
        // appears enabled even though IsDirty is false.
        _config = config;
        _isNewService = isNewService;
        // Pass the duplicate-name check into the draft so it can expose
        // a NameConflictHint live-bound to the inline error text under
        // the Name field. Excludes self via ReferenceEquals against
        // _config (the existing entry in App.Config.Services for an
        // Edit; not in the list yet for a new service).
        _draft = new ServiceConfigDraft(config, name =>
        {
            if (App.Config == null) return false;
            foreach (var s in App.Config.Config.Services)
            {
                if (ReferenceEquals(s, _config)) continue;
                if (string.Equals(s.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        });

        InitializeComponent();
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);

        DataContext = _draft;

        TitleBar.Title = isNewService
            ? "Add service"
            : $"Service settings — {config.Name}";

        // Forward Draft.IsDirty changes to our CanSave property so the
        // Save button binding (IsEnabled={Binding CanSave, ElementName=Root})
        // updates when the user edits anything.
        _draft.PropertyChanged += OnDraftPropertyChanged;

        // WPF Frame (used inside NavigationView) breaks DataContext inheritance
        // from the window down to hosted Pages. Force-assign on every navigation
        // so {Binding Name} etc. resolve against the draft.
        Nav.Navigated += OnNavigated;

        Closing += OnClosing;
        Loaded += (_, _) => Nav.Navigate(typeof(Pages.GeneralPage));

        Logger.Info($"[Settings] open id={config.Id} name='{config.Name}' " +
                    $"isNewService={isNewService} " +
                    $"url='{config.Url}' UA={config.UserAgent} " +
                    $"hotkey='{config.Hotkey ?? "(none)"}'");
    }

    private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Save button enabled state + its tooltip-reason both depend on
        // IsDirty + Name + Url + NameConflictHint. Re-evaluate when any
        // of those changes.
        if (e.PropertyName == nameof(ServiceConfigDraft.IsDirty) ||
            e.PropertyName == nameof(ServiceConfigDraft.Name) ||
            e.PropertyName == nameof(ServiceConfigDraft.Url) ||
            e.PropertyName == nameof(ServiceConfigDraft.NameConflictHint))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSave)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveBlockedReason)));
        }
    }

    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        if (args.Page is FrameworkElement fe)
        {
            fe.DataContext = _draft;
            Logger.Debug($"[Settings] navigated to {args.Page.GetType().Name}, DataContext set to draft");
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info($"[Settings] Save clicked for '{_config.Name}' " +
                    $"(isNewService={_isNewService}, isDirty={_draft.IsDirty}, " +
                    $"pendingIcon={(_draft.PendingIconUrl != null ? "yes" : "no")})");
        await ApplyAndPersistAsync();
        _explicitCloseDecision = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info($"[Settings] Cancel clicked for '{_config.Name}' " +
                    $"(isNewService={_isNewService}, isDirty={_draft.IsDirty})");
        // For new service: nothing to revert (was never added to config).
        // For existing: just close — _config was never mutated, edits live
        // only in the draft, draft will be GC'd.
        _explicitCloseDecision = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        LogCurrentSize("Closing");
        if (_explicitCloseDecision)
        {
            // Save_Click or Cancel_Click already chose. Allow close.
            return;
        }

        // X button (or Alt+F4). Only prompt when the user has real edits
        // to lose. For a brand-new service that the user opened and closed
        // without typing anything, we just discard silently — no service
        // ever lands in config (Save is the only path that adds it).
        if (!_draft.IsDirty)
        {
            return;
        }

        // Need an async dialog → cancel close for now, decide once user
        // picks an option in the Fluent MessageBox, then re-close (or not).
        e.Cancel = true;
        _ = ShowUnsavedChangesPromptAsync();
    }

    private async Task ShowUnsavedChangesPromptAsync()
    {
        // Don't offer Save when the data is invalid (empty Name/URL) —
        // otherwise users can bypass the Save-button guard via this dialog.
        var canSave = CanSave;
        string title;
        string prompt;
        Wpf.Ui.Controls.ContentDialog dialog;

        if (canSave)
        {
            title = "Unsaved changes";
            prompt = _isNewService
                ? "Save this new service before closing?"
                : "You have unsaved changes. Save before closing?";

            dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
            {
                Title = title,
                Content = prompt,
                DialogMaxWidth = 460,
                PrimaryButtonText = "Save",
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                SecondaryButtonText = "Discard",
                CloseButtonText = "Cancel",
            };
        }
        else
        {
            title = "Required fields are empty";
            prompt = "Name and URL are required to save. Discard your changes and close?";

            dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
            {
                Title = title,
                Content = prompt,
                DialogMaxWidth = 460,
                PrimaryButtonText = "Discard",
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Caution,
                CloseButtonText = "Cancel",
            };
        }

        Wpf.Ui.Controls.ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Settings] unsaved-changes dialog failed: {ex.Message}");
            return; // keep window open if dialog blew up
        }

        if (canSave)
        {
            switch (result)
            {
                case Wpf.Ui.Controls.ContentDialogResult.Primary:
                    await ApplyAndPersistAsync();
                    _explicitCloseDecision = true;
                    TryClose();
                    break;
                case Wpf.Ui.Controls.ContentDialogResult.Secondary:
                    _explicitCloseDecision = true;
                    TryClose();
                    break;
                // None (Cancel) → keep window open
            }
        }
        else
        {
            // Invalid-state dialog: Primary = Discard, Close = Cancel.
            if (result == Wpf.Ui.Controls.ContentDialogResult.Primary)
            {
                _explicitCloseDecision = true;
                TryClose();
            }
        }
    }

    /// <summary>
    /// Opens the macOSicons picker for this service. On Apply, the picker
    /// downloaded a new PNG into <c>Data/icons/{id}.png</c> — we refresh
    /// the open Hub + any open service window so the new icon appears
    /// without requiring the user to reopen anything.
    /// </summary>
    public void OpenIconPicker()
    {
        // Pass the DRAFT's name/URL, not the saved _config — the user may
        // have just edited the URL field (e.g. example.com → lolz.guru)
        // without saving yet; the picker's Standard tab needs the live
        // host to fetch the right favicon. Fallback to _config.Url only
        // when the draft URL is blank.
        var name = string.IsNullOrWhiteSpace(_draft.Name) ? _config.Name : _draft.Name;
        var url  = string.IsNullOrWhiteSpace(_draft.Url)  ? _config.Url  : _draft.Url;
        var picker = new IconPickerWindow(name, url) { Owner = this };
        var result = picker.ShowDialog();
        if (result == true && picker.SelectedHit != null)
        {
            var hit = picker.SelectedHit;
            // Priority order matches what the user already sees in the
            // picker preview (lowResPngUrl) so Hub tile / tray / taskbar
            // render the same macOS-style icon — not the raw full-bleed
            // iOS App Store asset that looked "zoomed in":
            //  1. icnsUrl      — extracted largest PNG frame (typically
            //                    1024 or 512px), macOS-rendered with the
            //                    artist's intended squircle + inset.
            //                    Same look as picker, much higher res.
            //  2. lowResPngUrl — 128px macOS-rendered fallback. Same look,
            //                    less detail at 4K-DPI taskbar.
            //  3. iOSUrl       — 1024px raw iOS app-icon, full-bleed.
            //                    Last resort when both macOS-style URLs
            //                    are dead (rare).
            var urls = new[] { hit.IcnsUrl, hit.LowResPngUrl, hit.IOSUrl }
                .Where(u => !string.IsNullOrEmpty(u))
                .Cast<string>()
                .ToList();
            if (urls.Count > 0)
            {
                Logger.Info($"[Settings] icon staged for '{_config.Name}' ← '{hit.AppName}' (downloads={hit.Downloads}). " +
                            $"{urls.Count} URL candidate(s). Download happens on Save; Cancel reverts.");
                _draft.SetPendingIcon(urls, picker.SelectedPreview);
            }
        }
    }

    private void TryClose()
    {
        try { Close(); }
        catch (InvalidOperationException ex)
        {
            // Window was already closing/closed. Nothing actionable.
            Logger.Debug($"[Settings] TryClose ignored: {ex.Message}");
        }
    }

    /// <summary>
    /// Captures the window's natural size for tuning the default
    /// Width/Height in XAML. <see cref="Window.RestoreBounds"/> is used
    /// so a maximized window still reports the size it was at before.
    /// </summary>
    private void LogCurrentSize(string reason)
    {
        var bounds = WindowState == WindowState.Normal
            ? new System.Windows.Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        Logger.Info($"[Settings] size on {reason}: Width={bounds.Width:F0} Height={bounds.Height:F0} (Left={bounds.Left:F0} Top={bounds.Top:F0})");
    }

    /// <summary>
    /// Snapshots the draft, copies into _config, and (for non-preview)
    /// adds new services to App.Config + reapplies + saves. When the
    /// draft has a <see cref="ServiceConfigDraft.PendingIconUrl"/>, the
    /// PNG is downloaded into Data/icons/{id}.png before ReapplyService
    /// so the Hub + service window pick up the new icon on refresh.
    /// </summary>
    private async Task ApplyAndPersistAsync()
    {
        // 1. Download the picker-staged icon, if any. Sequenced before
        //    CopyInto so the file is on disk by the time ReapplyService
        //    triggers ApplyConfigUpdate / Hub.RefreshAsync.
        var pendingUrls = _draft.PendingIconUrls;
        if (pendingUrls != null && pendingUrls.Count > 0)
        {
            try
            {
                var ok = await App.Favicon.ReplaceIconAsync(_config.Id, pendingUrls.ToArray());
                Logger.Info($"[Settings] pending icon downloaded for '{_config.Name}': {(ok ? "ok" : "FAILED")}");
                _draft.ClearPendingIcon();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Settings] pending icon download threw: {ex.Message}");
            }
        }

        var snap = _draft.Snapshot();
        var renamedFrom = !string.Equals(_config.Name, snap.Name, StringComparison.Ordinal)
            ? _config.Name
            : null;

        try
        {
            var before = JsonSerializer.Serialize(_config);
            CopyInto(_config, snap);
            var after = JsonSerializer.Serialize(_config);
            if (before == after && !_isNewService)
            {
                Logger.Debug($"[Settings] ApplyAndPersist no-op (snapshot equal to current config)");
            }
            else
            {
                Logger.Info($"[Settings] ApplyAndPersist → name='{_config.Name}' url='{_config.Url}' " +
                            $"UA={_config.UserAgent} hotkey='{_config.Hotkey ?? "(none)"}' " +
                            $"size={(int)_config.WindowWidth}x{(int)_config.WindowHeight} " +
                            $"rememberWin={_config.RememberWindowState} openCentered={_config.OpenCentered} " +
                            $"min2tray={_config.MinimizeToTray} close2tray={_config.CloseToTray} " +
                            $"showInTaskbar={_config.ShowInTaskbar} autostart={_config.AutoStart} " +
                            $"hasShortcut={_config.HasShortcut} translator={_config.IsTranslator} " +
                            $"protocol={_config.RegisterProtocol}/'{_config.ProtocolScheme ?? "-"}'");
                if (renamedFrom != null)
                    Logger.Info($"[Settings] rename detected: '{renamedFrom}' → '{snap.Name}'");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Settings] CopyInto/log failed: {ex.Message}");
            CopyInto(_config, snap);
        }

        try
        {
            // For new services, add to the live Services list now (Save is
            // when commitment happens — until now the svc was in-memory only).
            if (_isNewService && App.Config != null && !App.Config.Config.Services.Contains(_config))
            {
                App.Config.Config.Services.Add(_config);
                Logger.Info($"[Settings] new service added to config: '{_config.Name}'");
            }

            if (renamedFrom != null)
            {
                try
                {
                    ShortcutManager.Remove(renamedFrom);
                    Logger.Info($"[Settings] removed old shortcut for '{renamedFrom}'");
                }
                catch (Exception ex) { Logger.Warn($"Old shortcut cleanup '{renamedFrom}': {ex.Message}"); }
            }

            App.Windows?.ReapplyService(_config);
            Logger.Debug($"[Settings] ReapplyService done");
        }
        catch (Exception ex)
        {
            Logger.Warn($"ApplyAndPersist threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies every settable ServiceConfig field from src into dest.
    /// Keep in sync with ServiceConfigDraft's wrapped properties.
    /// </summary>
    private static void CopyInto(ServiceConfig dest, ServiceConfig src)
    {
        dest.Name = src.Name;
        dest.Url = src.Url;
        dest.UserAgent = src.UserAgent;
        dest.CustomUserAgent = src.CustomUserAgent;
        dest.ShowInTaskbar = src.ShowInTaskbar;
        dest.MinimizeToTray = src.MinimizeToTray;
        dest.CloseToTray = src.CloseToTray;
        dest.StartMinimized = src.StartMinimized;
        dest.CloseOnFocusLost = src.CloseOnFocusLost;
        dest.WindowWidth = src.WindowWidth;
        dest.WindowHeight = src.WindowHeight;
        dest.CustomIconPath = src.CustomIconPath;
        dest.Hotkey = src.Hotkey;
        dest.AutoStart = src.AutoStart;
        dest.HasShortcut = src.HasShortcut;
        dest.UnreadBadge = src.UnreadBadge;
        dest.UnreadRegex = src.UnreadRegex;
        dest.ZoomFactor = src.ZoomFactor;
        dest.IsTranslator = src.IsTranslator;
        dest.TranslatorTargetLang = src.TranslatorTargetLang;
        dest.UseDoubleCtrlC = src.UseDoubleCtrlC;
        dest.ProtocolScheme = src.ProtocolScheme;
        dest.RegisterProtocol = src.RegisterProtocol;
        dest.RememberWindowState = src.RememberWindowState;
        dest.OpenCentered = src.OpenCentered;
        dest.CustomCss = src.CustomCss;
        dest.CustomCssEnabled = src.CustomCssEnabled;
        dest.CustomCssOnlyInDarkTheme = src.CustomCssOnlyInDarkTheme;
        dest.VirtualHostName = src.VirtualHostName;
        dest.LocalFolderPath = src.LocalFolderPath;
    }
}
