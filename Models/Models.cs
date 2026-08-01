using System.Text.Json.Serialization;

namespace WebViewHub.Models;

public enum UserAgentMode
{
    Desktop,
    MobileIPhone,
    TabletIPad,
    Custom
}

public enum UnreadBadgeMode
{
    Off,
    TitleRegex
}

/// <summary>
/// One account inside a service — a separate WebView2 session (cookies,
/// localStorage, "logged in as"). Services start with a single implicit
/// profile; users add more from the service window's gear menu.
/// </summary>
public class ServiceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-facing label ("Work", "Personal"). Free to rename.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The WebView2 <c>ProfileName</c> — i.e. the on-disk session bucket
    /// (<c>webview2\EBWebView\WV2Profile_&lt;key&gt;</c>). Assigned once at
    /// creation and NEVER changed afterwards: renaming a profile must not
    /// point WebView2 at a different folder, or the user gets silently
    /// logged out. Always prefixed with the owning service's Id so two
    /// services can never collide on the same bucket.
    /// </summary>
    public string ProfileKey { get; set; } = "";
}

public class ServiceConfig
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public UserAgentMode UserAgent { get; set; } = UserAgentMode.Desktop;
    public string? CustomUserAgent { get; set; }
    public bool ShowInTaskbar { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = false;

    /// <summary>
    /// Auto-hide the window to the tray as soon as it loses focus. Useful
    /// for "peek" services (translator, quick search) you trigger via
    /// hotkey, glance at, then dismiss by clicking back to your work.
    /// Window stays alive so the next hotkey press is instant.
    /// </summary>
    public bool CloseOnFocusLost { get; set; } = false;
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 1200;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    /// <summary>
    /// When true, the window restores to its last saved Left/Top/Width/Height.
    /// When false, ignores saved position and opens at WindowWidth × WindowHeight
    /// at the system-default position. Default true preserves existing behavior.
    /// </summary>
    public bool RememberWindowState { get; set; } = true;

    /// <summary>
    /// When true, the window always opens centered on screen — its saved
    /// Left/Top are ignored on open and not persisted on close. Size
    /// remember/restore stays controlled by <see cref="RememberWindowState"/>.
    /// Useful for "peek" services that you trigger via hotkey from anywhere
    /// and want to land in the same predictable spot every time.
    /// </summary>
    public bool OpenCentered { get; set; } = true;

    /// <summary>
    /// Optional path to a custom icon file (.ico or .png).
    /// If null, favicon is auto-fetched.
    /// </summary>
    public string? CustomIconPath { get; set; }

    /// <summary>
    /// Global hotkey string, e.g. "Ctrl+Alt+S". Null/empty = none.
    /// </summary>
    public string? Hotkey { get; set; }

    /// <summary>
    /// Launch this service when Windows starts (HKCU Run entry).
    /// </summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// Whether a Start-menu shortcut for this service has been created.
    /// Toggling this on/off creates or deletes the .lnk file.
    /// </summary>
    public bool HasShortcut { get; set; }

    /// <summary>
    /// How to detect unread count for the tray-icon badge.
    /// </summary>
    public UnreadBadgeMode UnreadBadge { get; set; } = UnreadBadgeMode.Off;

    /// <summary>
    /// Regex applied to the document title when UnreadBadge = TitleRegex.
    /// First numeric capture group becomes the count.
    /// Default \((\d+)\) covers Slack/Gmail/Discord/Telegram/WhatsApp.
    /// </summary>
    public string? UnreadRegex { get; set; }

    /// <summary>
    /// Page zoom factor (1.0 = 100%). Persisted across sessions because
    /// WebView2 doesn't remember it on its own — user-set zoom would
    /// otherwise reset every time the window is recreated.
    /// </summary>
    public double ZoomFactor { get; set; } = 1.0;

    /// <summary>
    /// Marks this service as a translator. When opened via the translator
    /// trigger, the URL is rebuilt with the current clipboard text and
    /// the configured target language.
    /// </summary>
    public bool IsTranslator { get; set; }

    /// <summary>
    /// Target language code for the translator (e.g. "ru", "en", "de").
    /// Source is always auto-detected.
    /// </summary>
    public string? TranslatorTargetLang { get; set; } = "ru";

    /// <summary>
    /// When true, this service is triggered by a global double-tap of
    /// Ctrl+C (in addition to its regular global hotkey if any). Only
    /// meaningful when IsTranslator is true.
    /// </summary>
    public bool UseDoubleCtrlC { get; set; }

    /// <summary>
    /// URI scheme this service should claim system-wide (e.g. "spotify",
    /// "slack", "zoommtg"). When set and <see cref="RegisterProtocol"/>
    /// is true, HKCU\Software\Classes\&lt;scheme&gt; is rewritten on launch
    /// so that anything triggering "spotify:track:abc" routes here.
    /// </summary>
    public string? ProtocolScheme { get; set; }

    /// <summary>Toggle for the registry registration above. False = leave
    /// the OS handler alone (or actively unregister if it was ours).</summary>
    public bool RegisterProtocol { get; set; }

    /// <summary>
    /// User-supplied CSS injected on every page-load via WebView2
    /// AddScriptToExecuteOnDocumentCreatedAsync. Format expected: plain
    /// CSS (the inside of a style block; userstyles.world / Stylus
    /// snippets work as long as the user copies just the rules and not
    /// the @-moz-document wrapper). Only applied when
    /// <see cref="CustomCssEnabled"/> is true.
    /// </summary>
    public string? CustomCss { get; set; }

    /// <summary>Toggle to enable / disable <see cref="CustomCss"/>
    /// injection. Lets the user park unfinished or experimental CSS
    /// without losing it.</summary>
    public bool CustomCssEnabled { get; set; }

    /// <summary>
    /// When true, <see cref="CustomCss"/> is injected only while Windows
    /// is in dark mode; in light mode the style element is removed and
    /// the page renders unmodified. Lets users ship dark-theme tweaks
    /// (e.g. Google Translate dark redesign) without forcing them on
    /// users who switched their system back to light.
    /// </summary>
    public bool CustomCssOnlyInDarkTheme { get; set; }

    /// <summary>
    /// Virtual host name for WebView2's
    /// <c>SetVirtualHostNameToFolderMapping</c>. Lets a local-file
    /// service (e.g. a Project Hub dashboard <c>index.html</c>) load as
    /// <c>https://&lt;name&gt;/...</c> instead of <c>file://</c> — which
    /// matters because Chromium blocks <c>fetch()</c> / XHR across
    /// file:// pages (CORS for local files). Pair with
    /// <see cref="LocalFolderPath"/>. Set both = mapping applied;
    /// either null/empty = skipped entirely (most services don't need it).
    /// Example: <c>hub.local</c>.
    /// </summary>
    public string? VirtualHostName { get; set; }

    /// <summary>
    /// Absolute folder on disk to serve under <see cref="VirtualHostName"/>.
    /// Map the project ROOT, not a subfolder — the mapping cannot escape
    /// the mapped directory (no <c>..</c> resolution), so mapping <c>dist/</c>
    /// would hide siblings like <c>status/</c>.
    /// Example: <c>D:\projects\my-dashboard</c>.
    /// </summary>
    public string? LocalFolderPath { get; set; }

    /// <summary>
    /// Internal id used as folder name and WebView2 ProfileName.
    /// Generated once on creation, never changes when user renames.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Accounts available for this service. Empty in configs written before
    /// multi-profile support existed — <see cref="EnsureProfiles"/> fills in
    /// the implicit legacy profile on first access.
    /// </summary>
    public List<ServiceProfile> Profiles { get; set; } = new();

    /// <summary>Which entry of <see cref="Profiles"/> the window opens with.</summary>
    public string? ActiveProfileId { get; set; }

    /// <summary>
    /// Brings <see cref="Profiles"/> into a valid state and returns the active
    /// entry. Idempotent, so it is safe to call from config load AND lazily
    /// from the property getters — a service created at runtime is repaired
    /// the same way as one read from an old config file.
    /// </summary>
    public ServiceProfile EnsureProfiles()
    {
        if (Profiles.Count == 0)
        {
            // Legacy layout: the service's one and only session already lives
            // in the WebView2 profile named after Id. Adopt that exact name
            // instead of minting a fresh key — anything else would point
            // WebView2 at an empty folder and strand the existing login.
            Profiles.Add(new ServiceProfile { Name = "Default", ProfileKey = Id });
        }

        // Defensive: a hand-edited config can carry a profile with no key.
        // Mint a new one rather than falling back to Id, which might already
        // belong to another entry in the list.
        foreach (var p in Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.ProfileKey)) p.ProfileKey = NewProfileKey();
        }

        if (!Profiles.Any(p => p.Id == ActiveProfileId))
        {
            ActiveProfileId = Profiles[0].Id;
        }

        return Profiles.First(p => p.Id == ActiveProfileId);
    }

    /// <summary>
    /// Mints a session bucket name for a new profile. Service-Id prefix keeps
    /// buckets namespaced per service; the short suffix keeps the whole thing
    /// well under WebView2's 64-character ProfileName limit (32 + 1 + 8 = 41).
    /// </summary>
    public string NewProfileKey() => $"{Id}_{Guid.NewGuid().ToString("N")[..8]}";

    [JsonIgnore]
    public ServiceProfile ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? EnsureProfiles();

    /// <summary>
    /// Value handed to <c>CoreWebView2ControllerOptions.ProfileName</c>.
    /// For an un-migrated service this equals <see cref="Id"/>, which is
    /// exactly what previous versions used — that identity is what makes the
    /// upgrade invisible to existing logins.
    /// </summary>
    [JsonIgnore]
    public string EffectiveProfileKey => ActiveProfile.ProfileKey;

    [JsonIgnore]
    public string EffectiveUserAgent => UserAgent switch
    {
        UserAgentMode.MobileIPhone =>
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 " +
            "(KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        UserAgentMode.TabletIPad =>
            "Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 " +
            "(KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        UserAgentMode.Custom => CustomUserAgent ?? "",
        _ => "" // empty = WebView2 default (Edge desktop)
    };

    [JsonIgnore]
    public string EffectiveUnreadRegex =>
        string.IsNullOrWhiteSpace(UnreadRegex) ? @"\((\d+)\)" : UnreadRegex;
}

public class AppConfig
{
    public List<ServiceConfig> Services { get; set; } = new();
    public int Version { get; set; } = 1;

    /// <summary>
    /// Full path to the .exe at the time of the last run. Used on next
    /// startup to detect that the app was moved on disk so we can refresh
    /// shortcuts, autostart entries, and the URL protocol handler — all of
    /// which bake in the absolute exe path.
    /// </summary>
    public string? LastExePath { get; set; }

    /// <summary>
    /// App-wide settings that aren't tied to a specific service: API keys,
    /// hub launch behavior. Nested object so older config.json files
    /// (without this section) deserialize cleanly to a default instance.
    /// </summary>
    public HubSettings HubSettings { get; set; } = new();
}

/// <summary>
/// Which curated source <see cref="Services.FaviconService.GetIconAsync"/>
/// tries first for a new service. The favicon fallback chain runs after
/// the curated source misses regardless of which option is selected.
/// </summary>
public enum IconSource
{
    /// <summary>Skip curated sources, go straight to favicons
    /// (Google → DuckDuckGo → HTML hints → /favicon.ico). Default for
    /// users who want zero third-party-CDN traffic.</summary>
    Standard = 0,

    /// <summary>Try webcatalog.io's free curated 256-px webp icons
    /// (deterministic URL by slug, no API key). Falls back to favicons
    /// on 404.</summary>
    WebCatalog = 1,

    /// <summary>Try macOSicons.com (high-quality curated icons, requires
    /// a personal API key configured in Hub settings). Falls back to
    /// favicons on miss / network error / missing key.</summary>
    MacOSIcons = 2,
}

/// <summary>
/// App-wide preferences edited from the Hub's "Hub settings" window.
/// Stored alongside <see cref="ServiceConfig"/> entries in config.json.
/// </summary>
public class HubSettings
{
    /// <summary>
    /// API key for <see href="https://docs.macosicons.com"/>. When set,
    /// service icons are fetched from macOSicons first (high-quality
    /// curated app icons). On miss / empty key / network error we fall
    /// back to the existing favicon-fetch path.
    /// </summary>
    public string? MacOSIconsApiKey { get; set; }

    /// <summary>
    /// Which curated icon source <see cref="Services.FaviconService.GetIconAsync"/>
    /// tries first. The favicon fallback chain (Google → DuckDuckGo →
    /// HTML hints → /favicon.ico) ALWAYS runs after the curated source
    /// misses, so a 404 / no-hit on the curated step never leaves a
    /// service iconless. See <see cref="IconSource"/> for option semantics.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IconSource IconSource { get; set; } = IconSource.MacOSIcons;

    /// <summary>
    /// Whether to keep a Start-menu .lnk for the Hub itself. Default true
    /// matches the existing always-create behavior; toggling this off
    /// removes the .lnk on next save.
    /// </summary>
    public bool HasShortcut { get; set; } = true;

    /// <summary>
    /// Launch WebViewHub (the Hub itself, not any service) at Windows
    /// sign-in via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
    /// </summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// When <see cref="AutoStart"/> is on, start the Hub silently in the
    /// tray instead of showing the window on boot. Tray icon stays
    /// clickable so the user can surface the Hub on demand.
    /// </summary>
    public bool StartHidden { get; set; } = false;
}
