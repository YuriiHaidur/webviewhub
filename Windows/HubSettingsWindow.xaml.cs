using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using WebViewHub.Models;
using WebViewHub.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
// Disambiguate vs Wpf.Ui.Controls.IconSource — that's an unrelated WPF-UI
// icon-source type; we always mean the curated-provider enum from Models.
using IconSource = WebViewHub.Models.IconSource;

namespace WebViewHub.Windows;

/// <summary>
/// App-wide settings dialog for the Hub itself: macOSicons API key,
/// Hub start-menu shortcut, Hub autostart with Windows, start-hidden flag.
/// Reads/writes <see cref="AppConfig.HubSettings"/> via a working copy so
/// Cancel discards cleanly.
/// </summary>
public partial class HubSettingsWindow : FluentWindow, INotifyPropertyChanged
{
    private readonly HubSettings _original;

    // Snapshot of the values shown when the dialog opened. Used to
    // compute IsDirty / CanSave so the Save button only lights up when
    // the user actually changed something. Same pattern as
    // ServiceSettingsWindow (ServiceConfigDraft.IsDirty).
    private readonly IconSource _origIconSource;
    private readonly string? _origMacOSIconsApiKey;
    private readonly bool   _origHasShortcut;
    private readonly bool   _origHubAutoStart;
    private readonly bool   _origHubStartHidden;

    private IconSource _iconSource;
    private string? _macOSIconsApiKey;
    private bool _hasShortcut;
    private bool _hubAutoStart;
    private bool _hubStartHidden;

    /// <summary>True after Save_Click or Cancel_Click — tells OnClosing
    /// not to re-prompt because the user already made a choice.</summary>
    private bool _explicitCloseDecision;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IconSource IconSource
    {
        get => _iconSource;
        set
        {
            if (_iconSource == value) return;
            _iconSource = value;
            // Notify the value setter + the derived API-key-visibility prop
            // + IsDirty/CanSave so the Save button reactively updates and
            // the API-key card hides/shows.
            Notify(nameof(IconSource));
            Notify(nameof(IsMacOSIconsSelected));
            Notify(nameof(IsDirty));
            Notify(nameof(CanSave));
        }
    }

    /// <summary>Drives visibility of the macOSicons API-key card —
    /// only meaningful when MacOSIcons is the selected source.</summary>
    public bool IsMacOSIconsSelected => _iconSource == IconSource.MacOSIcons;

    public string? MacOSIconsApiKey
    {
        get => _macOSIconsApiKey;
        set
        {
            _macOSIconsApiKey = value;
            // Diagnostic: catches the "TextBox-binding-silently-dropped"
            // failure mode we just fixed. Don't log the key value itself —
            // length only — so a screenshot of the log isn't a credentials leak.
            Logger.Debug($"[HubSettings] MacOSIconsApiKey setter fired, length={value?.Length ?? 0}");
            NotifyValueAndDirty();
        }
    }

    public bool HasShortcut
    {
        get => _hasShortcut;
        set { _hasShortcut = value; NotifyValueAndDirty(); }
    }

    public bool HubAutoStart
    {
        get => _hubAutoStart;
        set { _hubAutoStart = value; NotifyValueAndDirty(); }
    }

    public bool HubStartHidden
    {
        get => _hubStartHidden;
        set { _hubStartHidden = value; NotifyValueAndDirty(); }
    }

    /// <summary>True when any visible field differs from the snapshot
    /// captured at dialog-open time. Compared <see cref="MacOSIconsApiKey"/>
    /// strings are normalized (null ≡ "") so leading/trailing whitespace
    /// pasted into an empty field doesn't enable Save by itself — but the
    /// same key already saved equals the snapshot.</summary>
    public bool IsDirty =>
        IconSource != _origIconSource
        || !string.Equals(NormalizeKey(_macOSIconsApiKey), NormalizeKey(_origMacOSIconsApiKey),
                           System.StringComparison.Ordinal)
        || HasShortcut != _origHasShortcut
        || HubAutoStart != _origHubAutoStart
        || HubStartHidden != _origHubStartHidden;

    /// <summary>Save button enabled state. No required fields in Hub
    /// settings — every value has a meaningful default — so CanSave just
    /// tracks IsDirty.</summary>
    public bool CanSave => IsDirty;

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    public HubSettingsWindow(HubSettings current)
    {
        _original = current;

        _origIconSource       = current.IconSource;
        _origMacOSIconsApiKey = current.MacOSIconsApiKey;
        _origHasShortcut      = current.HasShortcut;
        _origHubAutoStart     = current.AutoStart;
        _origHubStartHidden   = current.StartHidden;

        _iconSource       = _origIconSource;
        _macOSIconsApiKey = _origMacOSIconsApiKey;
        _hasShortcut      = _origHasShortcut;
        _hubAutoStart     = _origHubAutoStart;
        _hubStartHidden   = _origHubStartHidden;

        InitializeComponent();
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        DataContext = this;

        Closing += OnClosing;

        Logger.Info("[HubSettings] open");
    }

    private void Notify([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    /// <summary>Fires INPC for the property setter that called this AND
    /// for IsDirty/CanSave so the Save button reactively enables/disables.</summary>
    private void NotifyValueAndDirty([CallerMemberName] string? prop = null)
    {
        Notify(prop);
        Notify(nameof(IsDirty));
        Notify(nameof(CanSave));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info(
            $"[HubSettings] Save → " +
            $"iconSource={IconSource} hasKey={!string.IsNullOrWhiteSpace(MacOSIconsApiKey)} " +
            $"hasShortcut={HasShortcut} autoStart={HubAutoStart} startHidden={HubStartHidden}");

        _original.IconSource = IconSource;
        _original.MacOSIconsApiKey = string.IsNullOrWhiteSpace(MacOSIconsApiKey)
            ? null
            : MacOSIconsApiKey.Trim();
        _original.HasShortcut = HasShortcut;
        _original.AutoStart = HubAutoStart;
        _original.StartHidden = HubStartHidden;

        try { App.Config.SaveSync(); }
        catch (System.Exception ex) { Logger.Warn($"[HubSettings] config save failed: {ex.Message}"); }

        // Side effects: shortcut + autostart match the new flags.
        App.ApplyHubLaunchSettings();

        _explicitCloseDecision = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info($"[HubSettings] Cancel (isDirty={IsDirty})");
        _explicitCloseDecision = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_explicitCloseDecision) return;            // Save/Cancel already decided.
        if (!IsDirty) return;                          // Nothing to lose.

        // X button (or Alt+F4) with pending edits — confirm via Fluent
        // dialog instead of slamming changes shut. Mirrors the prompt
        // in ServiceSettingsWindow.ShowUnsavedChangesPromptAsync.
        e.Cancel = true;
        _ = ShowUnsavedChangesPromptAsync();
    }

    private async Task ShowUnsavedChangesPromptAsync()
    {
        var dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
        {
            Title = "Unsaved changes",
            Content = "You have unsaved changes to Hub settings. Save before closing?",
            DialogMaxWidth = 460,
            PrimaryButtonText = "Save",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
        };

        var result = await dialog.ShowAsync();
        switch (result)
        {
            case Wpf.Ui.Controls.ContentDialogResult.Primary:    // Save
                Save_Click(this, new RoutedEventArgs());
                break;
            case Wpf.Ui.Controls.ContentDialogResult.Secondary:  // Discard
                _explicitCloseDecision = true;
                Close();
                break;
            // None (Cancel): stay open, user keeps editing.
        }
    }

    /// <summary>
    /// Lets the user click the CardExpander header toggle (which sits
    /// inside the expander's clickable header area) without the click
    /// also collapsing/expanding the panel.
    /// </summary>
    private void HeaderToggle_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is ToggleSwitch t) t.IsChecked = !t.IsChecked;
    }
}
