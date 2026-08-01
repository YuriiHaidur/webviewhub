using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using WebViewHub.Helpers;

namespace WebViewHub.Models;

/// <summary>
/// Editable working copy of a <see cref="ServiceConfig"/>. Wraps every
/// settable property with INotifyPropertyChanged + a single Changed event
/// (used by ServiceSettingsWindow to drive a debounced auto-apply timer).
/// Discard reverts to the original snapshot taken at construction.
/// </summary>
public class ServiceConfigDraft : INotifyPropertyChanged
{
    private readonly ServiceConfig _original;
    private ServiceConfig _current;
    private readonly Func<string, bool>? _isNameTakenByAnother;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fires after every property change (post-PropertyChanged).
    /// Used by the settings window to restart its debounce timer.</summary>
    public event Action? Changed;

    public ServiceConfigDraft(ServiceConfig src, Func<string, bool>? isNameTakenByAnother = null)
    {
        _isNameTakenByAnother = isNameTakenByAnother;
        // Defensively coerce values that the UI's NumberBox / ComboBox
        // would otherwise display incorrectly. A corrupt/zeroed config
        // field shows the NumberBox's Min visually but the binding stays
        // at the bad value until the user touches it — save round-trip
        // would persist the bad value. Coerce on the source clone for
        // BOTH original + current so IsDirty stays false at start.
        var coerced = Clone(src);
        if (coerced.WindowWidth < 300 || coerced.WindowWidth > 5000)
            coerced.WindowWidth = 1200;
        if (coerced.WindowHeight < 300 || coerced.WindowHeight > 5000)
            coerced.WindowHeight = 800;
        if (string.IsNullOrWhiteSpace(coerced.TranslatorTargetLang))
            coerced.TranslatorTargetLang = "ru";

        _original = Clone(coerced);
        _current = Clone(coerced);
    }

    /// <summary>
    /// True when either the wrapped <see cref="ServiceConfig"/> fields have
    /// been edited OR the icon picker has staged a new icon URL waiting
    /// for Save. The pending icon URL isn't part of ServiceConfig so we
    /// have to include it here explicitly — otherwise picking an icon
    /// wouldn't light up Save.
    /// </summary>
    public bool IsDirty => Serialize(_original) != Serialize(_current) || _pendingIconUrls != null;

    private List<string>? _pendingIconUrls;
    private ImageSource? _pendingIconImage;

    /// <summary>
    /// Candidate URLs (priority order) for the icon the user picked from
    /// the macOSicons dialog, or null if no pick happened. Read by
    /// ApplyAndPersist which tries each URL in order until one downloads
    /// successfully — needed because some hits have a 403'ing iOSUrl and
    /// we fall back to the smaller lowResPngUrl.
    /// </summary>
    public IReadOnlyList<string>? PendingIconUrls => _pendingIconUrls;

    /// <summary>Legacy single-URL accessor kept for callers that only
    /// care whether anything is pending — returns the highest-priority
    /// (first) candidate.</summary>
    public string? PendingIconUrl => _pendingIconUrls?.FirstOrDefault();

    /// <summary>
    /// What the "Icon" card on the General page should display: the
    /// preview thumbnail of the user's pending pick (if any), otherwise
    /// the live icon currently cached on disk for this service.
    /// </summary>
    public ImageSource? IconPreview
    {
        get
        {
            if (_pendingIconImage != null) return _pendingIconImage;
            try
            {
                var path = Path.Combine(App.Paths.IconsDir, $"{_current.Id}.png");
                return IconHelper.LoadWpfImage(path)
                       ?? IconHelper.GenerateLetterImage(_current.Name ?? "?");
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Stages a new icon for Save. The picker passes both the
    /// full-resolution URL (downloaded on Save by ApplyAndPersist) and
    /// the thumbnail it already loaded for the card grid, which doubles
    /// as the preview shown on the General page while the user is still
    /// editing.
    /// </summary>
    public void SetPendingIcon(IEnumerable<string> urls, ImageSource? preview)
    {
        _pendingIconUrls = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (_pendingIconUrls.Count == 0) _pendingIconUrls = null;
        _pendingIconImage = preview;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingIconUrl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingIconUrls)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
        Changed?.Invoke();
    }

    /// <summary>Called by ApplyAndPersist after a successful icon write
    /// to disk so subsequent IsDirty checks aren't kept hot by a stale
    /// pending state.</summary>
    public void ClearPendingIcon()
    {
        _pendingIconUrls = null;
        _pendingIconImage = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingIconUrl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingIconUrls)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
    }

    /// <summary>
    /// Non-null hint to show under the Name field when the chosen Name
    /// collides with another existing service. Returns null when the Name
    /// is valid (no callback installed, empty Name, or unique Name).
    /// </summary>
    public string? NameConflictHint
    {
        get
        {
            var name = _current.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            if (_isNameTakenByAnother == null) return null;
            return _isNameTakenByAnother(name)
                ? $"A service named \"{name}\" already exists"
                : null;
        }
    }

    public ServiceConfig Snapshot() => Clone(_current);

    public void Discard()
    {
        _current = Clone(_original);
        _pendingIconUrls = null;
        _pendingIconImage = null;
        // Re-fire INPC for every property the UI might be bound to.
        // We don't track which actually differed — easier to fire all.
        foreach (var prop in DraftProperties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingIconUrl)));
    }

    private static readonly string[] DraftProperties =
    {
        nameof(Name), nameof(Url), nameof(UserAgent), nameof(CustomUserAgent),
        nameof(ShowInTaskbar), nameof(MinimizeToTray), nameof(CloseToTray),
        nameof(StartMinimized), nameof(CloseOnFocusLost),
        nameof(WindowWidth), nameof(WindowHeight), nameof(RememberWindowState), nameof(OpenCentered),
        nameof(CustomIconPath), nameof(Hotkey),
        nameof(AutoStart), nameof(HasShortcut),
        nameof(UnreadBadge), nameof(UnreadRegex),
        nameof(ZoomFactor),
        nameof(IsTranslator), nameof(TranslatorTargetLang), nameof(UseDoubleCtrlC),
        nameof(ProtocolScheme), nameof(RegisterProtocol),
        nameof(CustomCss), nameof(CustomCssEnabled), nameof(CustomCssOnlyInDarkTheme),
        nameof(VirtualHostName), nameof(LocalFolderPath),
    };

    private bool SetEqual<T>(T existing, T value)
    {
        return EqualityComparer<T>.Default.Equals(existing, value);
    }

    private void Notify([CallerMemberName] string? prop = null)
    {
        // IsDirty changes whenever any field is mutated — fire its INPC
        // so Save button binding (IsEnabled={Binding IsDirty}) updates.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        Changed?.Invoke();
    }

    public string Name
    {
        get => _current.Name;
        set
        {
            if (SetEqual(_current.Name, value)) return;
            _current.Name = value;
            Notify();
            // NameConflictHint depends on Name — fire its INPC too so the
            // inline error text updates as the user types.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameConflictHint)));
        }
    }

    public string Url
    {
        get => _current.Url;
        set { if (SetEqual(_current.Url, value)) return; _current.Url = value; Notify(); }
    }

    public UserAgentMode UserAgent
    {
        get => _current.UserAgent;
        set { if (SetEqual(_current.UserAgent, value)) return; _current.UserAgent = value; Notify(); }
    }

    public string? CustomUserAgent
    {
        get => _current.CustomUserAgent;
        set { if (SetEqual(_current.CustomUserAgent, value)) return; _current.CustomUserAgent = value; Notify(); }
    }

    public bool ShowInTaskbar
    {
        get => _current.ShowInTaskbar;
        set { if (SetEqual(_current.ShowInTaskbar, value)) return; _current.ShowInTaskbar = value; Notify(); }
    }

    public bool MinimizeToTray
    {
        get => _current.MinimizeToTray;
        set
        {
            if (SetEqual(_current.MinimizeToTray, value)) return;
            _current.MinimizeToTray = value;
            Notify();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideToTrayMaster)));
        }
    }

    public bool CloseToTray
    {
        get => _current.CloseToTray;
        set
        {
            if (SetEqual(_current.CloseToTray, value)) return;
            _current.CloseToTray = value;
            Notify();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideToTrayMaster)));
        }
    }

    /// <summary>
    /// Synthetic "any tray hiding on" master toggle.
    /// Get: true when at least one of MinimizeToTray / CloseToTray is on.
    /// Set: writes both fields to the new value (granular tweak still
    /// available via the two sub-toggles inside the expander).
    /// </summary>
    public bool HideToTrayMaster
    {
        get => _current.MinimizeToTray || _current.CloseToTray;
        set
        {
            if (HideToTrayMaster == value) return;
            _current.MinimizeToTray = value;
            _current.CloseToTray = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MinimizeToTray)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseToTray)));
            Notify();
        }
    }

    public bool StartMinimized
    {
        get => _current.StartMinimized;
        set { if (SetEqual(_current.StartMinimized, value)) return; _current.StartMinimized = value; Notify(); }
    }

    public bool CloseOnFocusLost
    {
        get => _current.CloseOnFocusLost;
        set { if (SetEqual(_current.CloseOnFocusLost, value)) return; _current.CloseOnFocusLost = value; Notify(); }
    }

    public double WindowWidth
    {
        get => _current.WindowWidth;
        set
        {
            if (SetEqual(_current.WindowWidth, value)) return;
            _current.WindowWidth = value;
            Notify();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowSizeDisplay)));
        }
    }

    public double WindowHeight
    {
        get => _current.WindowHeight;
        set
        {
            if (SetEqual(_current.WindowHeight, value)) return;
            _current.WindowHeight = value;
            Notify();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowSizeDisplay)));
        }
    }

    /// <summary>Live "1200 × 800 px" string for the Window-size expander header.</summary>
    public string WindowSizeDisplay => $"{(int)_current.WindowWidth} × {(int)_current.WindowHeight} px";

    public bool RememberWindowState
    {
        get => _current.RememberWindowState;
        set { if (SetEqual(_current.RememberWindowState, value)) return; _current.RememberWindowState = value; Notify(); }
    }

    public bool OpenCentered
    {
        get => _current.OpenCentered;
        set { if (SetEqual(_current.OpenCentered, value)) return; _current.OpenCentered = value; Notify(); }
    }

    public string? CustomIconPath
    {
        get => _current.CustomIconPath;
        set { if (SetEqual(_current.CustomIconPath, value)) return; _current.CustomIconPath = value; Notify(); }
    }

    public string? Hotkey
    {
        get => _current.Hotkey;
        set
        {
            if (SetEqual(_current.Hotkey, value)) return;
            _current.Hotkey = value;
            Notify();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HotkeyDisplay)));
        }
    }

    /// <summary>Live "Ctrl + Alt + S" / "Not set" string for the Hotkey expander header.</summary>
    public string HotkeyDisplay =>
        string.IsNullOrWhiteSpace(_current.Hotkey) ? "Not set" : _current.Hotkey!.Replace("+", " + ");

    public bool AutoStart
    {
        get => _current.AutoStart;
        set { if (SetEqual(_current.AutoStart, value)) return; _current.AutoStart = value; Notify(); }
    }

    public bool HasShortcut
    {
        get => _current.HasShortcut;
        set { if (SetEqual(_current.HasShortcut, value)) return; _current.HasShortcut = value; Notify(); }
    }

    public UnreadBadgeMode UnreadBadge
    {
        get => _current.UnreadBadge;
        set { if (SetEqual(_current.UnreadBadge, value)) return; _current.UnreadBadge = value; Notify(); }
    }

    public string? UnreadRegex
    {
        get => _current.UnreadRegex;
        set { if (SetEqual(_current.UnreadRegex, value)) return; _current.UnreadRegex = value; Notify(); }
    }

    public double ZoomFactor
    {
        get => _current.ZoomFactor;
        set { if (SetEqual(_current.ZoomFactor, value)) return; _current.ZoomFactor = value; Notify(); }
    }

    public bool IsTranslator
    {
        get => _current.IsTranslator;
        set { if (SetEqual(_current.IsTranslator, value)) return; _current.IsTranslator = value; Notify(); }
    }

    public string? TranslatorTargetLang
    {
        get => _current.TranslatorTargetLang;
        set { if (SetEqual(_current.TranslatorTargetLang, value)) return; _current.TranslatorTargetLang = value; Notify(); }
    }

    public bool UseDoubleCtrlC
    {
        get => _current.UseDoubleCtrlC;
        set { if (SetEqual(_current.UseDoubleCtrlC, value)) return; _current.UseDoubleCtrlC = value; Notify(); }
    }

    public string? ProtocolScheme
    {
        get => _current.ProtocolScheme;
        set { if (SetEqual(_current.ProtocolScheme, value)) return; _current.ProtocolScheme = value; Notify(); }
    }

    public bool RegisterProtocol
    {
        get => _current.RegisterProtocol;
        set { if (SetEqual(_current.RegisterProtocol, value)) return; _current.RegisterProtocol = value; Notify(); }
    }

    public string? CustomCss
    {
        get => _current.CustomCss;
        set { if (SetEqual(_current.CustomCss, value)) return; _current.CustomCss = value; Notify(); }
    }

    public bool CustomCssEnabled
    {
        get => _current.CustomCssEnabled;
        set { if (SetEqual(_current.CustomCssEnabled, value)) return; _current.CustomCssEnabled = value; Notify(); }
    }

    public bool CustomCssOnlyInDarkTheme
    {
        get => _current.CustomCssOnlyInDarkTheme;
        set { if (SetEqual(_current.CustomCssOnlyInDarkTheme, value)) return; _current.CustomCssOnlyInDarkTheme = value; Notify(); }
    }

    public string? VirtualHostName
    {
        get => _current.VirtualHostName;
        set { if (SetEqual(_current.VirtualHostName, value)) return; _current.VirtualHostName = value; Notify(); }
    }

    public string? LocalFolderPath
    {
        get => _current.LocalFolderPath;
        set { if (SetEqual(_current.LocalFolderPath, value)) return; _current.LocalFolderPath = value; Notify(); }
    }

    private static ServiceConfig Clone(ServiceConfig s) =>
        JsonSerializer.Deserialize<ServiceConfig>(JsonSerializer.Serialize(s))!;

    private static string Serialize(ServiceConfig s) => JsonSerializer.Serialize(s);
}
