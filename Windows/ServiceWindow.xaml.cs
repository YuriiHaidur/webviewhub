using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WebViewHub.Helpers;
using WebViewHub.Models;
using WebViewHub.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace WebViewHub.Windows;

public partial class ServiceWindow : FluentWindow
{
    private ServiceConfig _config;
    private TaskbarIcon? _trayIcon;
    private bool _forceClosing;

    /// <summary>Cached base bitmap (no badge). Used to recompose icons
    /// when the unread count changes.</summary>
    private Bitmap? _baseIconBitmap;
    /// <summary>Source PNG path for Window.Icon. Set alongside
    /// _baseIconBitmap so we can hand the file straight to WPF instead of
    /// going through GDI+ (which shifts colours on transparent edges).</summary>
    private string? _iconSourcePath;
    private int _unreadCount;

    /// <summary>Debounces zoom-level writes — user can wheel through a
    /// dozen levels in a second; we wait until they stop, then save.</summary>
    private DispatcherTimer? _zoomSaveTimer;

    /// <summary>If set before WebView2 is ready, this URL is navigated to
    /// instead of <see cref="ServiceConfig.Url"/> on first init. Used by
    /// the translator flow so the very first load already contains the
    /// clipboard text.</summary>
    private string? _pendingNavigationUrl;

    /// <summary>When true, the window hides to tray after WebView2
    /// finishes initializing. Set by <see cref="RequestHideAfterInit"/>
    /// so the caller can ask for "start minimized" without breaking
    /// WebView2's need for a visible HWND during init.</summary>
    private bool _hideAfterInit;

    /// <summary>True between <see cref="PrepareHiddenStart"/> and the
    /// post-init position restore. While set, the window is parked off
    /// the visible desktop so the user never sees the WebView2 init
    /// flash on autostart.</summary>
    private bool _hiddenStartParked;

    /// <summary>HICONs assigned via WM_SETICON to give the taskbar /
    /// titlebar / Alt+Tab slots DPI-correct frames straight out of the
    /// multi-frame .ico. Owned by this window — must be DestroyIcon'd on
    /// replace and on Closed. WPF's own Window.Icon path is left alone
    /// (it still feeds Alt+Tab thumbnails).</summary>
    private IntPtr _hIconSmall = IntPtr.Zero;
    private IntPtr _hIconBig = IntPtr.Zero;

    /// <summary>Script id returned by AddScriptToExecuteOnDocumentCreatedAsync
    /// for the custom CSS injection. Tracked so we can RemoveScript on
    /// config change (toggle off, edit body) before re-adding a fresh one.</summary>
    private string? _customCssScriptId;

    public ServiceWindow(ServiceConfig config)
    {
        Logger.Info($"[{config.Name}] ServiceWindow ctor — InitializeComponent.");
        InitializeComponent();
        _config = config;

        Logger.Info($"[{config.Name}] ServiceWindow ctor — applying config.");
        Title = ComposeTitle(null);
        Width = config.WindowWidth;
        Height = config.WindowHeight;
        ShowInTaskbar = config.ShowInTaskbar;

        // OpenCentered overrides any saved Left/Top — the window always
        // lands at the screen center on open (size still restored when
        // RememberWindowState is on).
        if (config.RememberWindowState && !config.OpenCentered
            && config.WindowLeft.HasValue && config.WindowTop.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = config.WindowLeft.Value;
            Top = config.WindowTop.Value;
        }

        // Window icon — start with a generated letter, replaced once favicon loads.
        Logger.Info($"[{config.Name}] ServiceWindow ctor — generate placeholder icon.");
        Icon = IconHelper.GenerateLetterImage(config.Name);

        Logger.Info($"[{config.Name}] ServiceWindow ctor — TaskbarItemInfo.");
        TaskbarItemInfo = new TaskbarItemInfo();

        SystemThemeWatcher.Watch(this, WindowBackdropType.Auto, updateAccents: true);

        UpdateTitleBarIconColors();
        ApplicationThemeManager.Changed += OnAppThemeChanged;

        Logger.Info($"[{config.Name}] ServiceWindow ctor — wiring events.");
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) =>
        {
            Logger.Info($"[{_config.Name}] Loaded event — entering InitializeAsync.");
            try { await InitializeAsync(); }
            catch (Exception ex) { Logger.Error($"[{_config.Name}] InitializeAsync threw outside try", ex); }
        };
        StateChanged += OnStateChanged;
        Activated += OnActivatedClearBadge;
        Activated += OnActivatedTrackUsage;
        Deactivated += OnDeactivatedAutoHide;
        Deactivated += OnDeactivatedTrackUsage;
        Closing += OnClosing;

        // Usage tracking — single per-window-lifetime "open" event. Focus
        // intervals come from Activated/Deactivated, and explicit close is
        // emitted from OnClosing when the user really closes (vs hide-to-tray).
        App.Usage?.TrackOpened(_config.Id, _config.Name);
        // Per-monitor DPI changes mean a different tray-slot / taskbar
        // icon size — reload HICONs from the .ico so the new monitor
        // sees a frame matched to its scale factor instead of a
        // stretched/squished left-over from the previous monitor.
        DpiChanged += OnDpiChanged;
        Logger.Info($"[{config.Name}] ServiceWindow ctor — done.");
    }

    /// <summary>
    /// Picks up edits saved from the hub while this window is open.
    /// Re-applies title, taskbar visibility, user agent and badge mode.
    /// </summary>
    public void ApplyConfigUpdate(ServiceConfig updated)
    {
        var oldUrl = _config.Url;
        _config = updated;

        Title = ComposeTitle(null);
        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = updated.Name.Length > 63 ? updated.Name[..63] : updated.Name;
        }
        ShowInTaskbar = updated.ShowInTaskbar;

        try
        {
            if (WebView.CoreWebView2 != null)
            {
                var ua = updated.EffectiveUserAgent;
                WebView.CoreWebView2.Settings.UserAgent = ua;

                if (oldUrl != updated.Url && !string.IsNullOrWhiteSpace(updated.Url))
                {
                    WebView.CoreWebView2.Navigate(updated.Url);
                }
            }

            // Push live zoom changes; otherwise zoom only takes effect on
            // the next window open.
            if (Math.Abs(WebView.ZoomFactor - updated.ZoomFactor) > 0.001)
            {
                WebView.ZoomFactor = updated.ZoomFactor;
            }

            // Re-apply custom CSS (no-op if unchanged would also be fine —
            // ApplyCustomCssAsync handles add/remove based on current
            // config). Reload so the active page picks up the new style;
            // AddScriptToExecuteOnDocumentCreatedAsync only runs on next
            // document creation by itself.
            _ = ApplyCustomCssAsync().ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    try { WebView.CoreWebView2?.Reload(); } catch { }
                });
            });
        }
        catch { /* best effort */ }

        // Refresh icon from disk — the cached file may have been replaced
        // by the icon picker since this window was last initialized. Both
        // the tray icon (via _baseIconBitmap) and Window.Icon (taskbar)
        // are affected. Window.Icon goes through LoadWpfImageScaled so
        // the taskbar HICON downscale starts from a clean 256px source
        // instead of the full 1024px file — much sharper edges.
        try
        {
            var iconPath = !string.IsNullOrEmpty(updated.CustomIconPath)
                ? updated.CustomIconPath
                : System.IO.Path.Combine(App.Paths.IconsDir, $"{updated.Id}.png");
            if (System.IO.File.Exists(iconPath))
            {
                LoadBaseIcon(iconPath);
                var wpfImg = IconHelper.LoadWpfImageScaled(iconPath, 256)
                             ?? (System.Windows.Media.ImageSource?)IconHelper.LoadWpfImage(iconPath);
                if (wpfImg != null) Icon = wpfImg;
                // Re-send WM_SETICON with HICONs pulled from the freshly
                // generated .ico so titlebar / taskbar / Alt+Tab pick up
                // the new icon (WPF Window.Icon assignment above only
                // updates Alt+Tab thumbnail in modern Windows shells).
                ApplyMultiDpiIcons();
            }
        }
        catch (Exception ex) { Logger.Warn($"[{_config.Name}] icon reload in ApplyConfigUpdate: {ex.Message}"); }

        // Reset unread count if user toggled the badge mode off.
        if (updated.UnreadBadge == UnreadBadgeMode.Off && _unreadCount != 0)
        {
            UpdateUnreadCount(0);
        }
        else
        {
            // Re-render with current count under whatever rules are now active.
            // Also picks up the freshly-loaded _baseIconBitmap above.
            ApplyUnreadVisuals();
        }

        // Name changed → update the taskbar pin identity so a future
        // pin captures the new name/icon.
        ApplyTaskbarIdentity();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Logger.Info($"[{_config.Name}] OnSourceInitialized — start.");
        // Apply DWM cloak as early as possible — HWND just got created
        // and WPF hasn't called ShowWindow yet, so the window never
        // becomes visible to the user.
        if (_hiddenStartParked)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ok = NativeMethods.SetCloak(hwnd, true);
            Logger.Debug($"[{_config.Name}] OnSourceInitialized — DWM cloak applied: {ok}.");
        }
        ApplyTaskbarIdentity();
        // WM_SETICON must run AFTER WPF posts its own ICON_SMALL/ICON_BIG
        // from the Window.Icon property — otherwise WPF overwrites our
        // handles. OnSourceInitialized fires after WPF has assigned its
        // single-frame HICON, so our DPI-aware HICONs win.
        ApplyMultiDpiIcons();
        Logger.Info($"[{_config.Name}] OnSourceInitialized — done.");
    }

    /// <summary>
    /// Tells Windows what to use when the user pins this window to the
    /// taskbar — AUMID + relaunch command + display name + icon. Without
    /// the last three, every pinned service would re-launch the bare exe
    /// and show "WebViewHub" with a generic icon.
    /// </summary>
    private void ApplyTaskbarIdentity()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                Logger.Warn($"[{_config.Name}] ApplyTaskbarIdentity — HWND is zero.");
                return;
            }

            var appId = $"WebViewHub.Service.{_config.Id}";
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                          ?? System.IO.Path.Combine(AppContext.BaseDirectory, "WebViewHub.exe");
            var relaunchCommand = $"\"{exePath}\" --service=\"{_config.Id}\"";
            var iconResource = ResolveIconResource();
            Logger.Debug($"[{_config.Name}] ApplyTaskbarIdentity icon={iconResource ?? "(none)"}");

            NativeMethods.SetWindowAppProperties(hwnd, appId, relaunchCommand, _config.Name, iconResource);
        }
        catch (Exception ex)
        {
            Logger.Error($"[{_config.Name}] ApplyTaskbarIdentity failed", ex);
        }
    }

    private void OnDpiChanged(object? sender, System.Windows.DpiChangedEventArgs e)
    {
        Logger.Debug($"[{_config.Name}] DPI changed {e.OldDpi.PixelsPerInchX} → {e.NewDpi.PixelsPerInchX}");
        ApplyMultiDpiIcons();
        ApplyUnreadVisuals();
    }

    /// <summary>
    /// Sends WM_SETICON with DPI-aware HICONs pulled straight from the
    /// service's multi-frame .ico (via Win32 LoadImage). This gives the
    /// titlebar / taskbar / Alt+Tab slots a native pixel-perfect frame
    /// instead of WPF's single-size HICON downscale. Idempotent — safe
    /// to call from OnSourceInitialized, after icon replacement, and on
    /// DpiChanged.
    /// </summary>
    private void ApplyMultiDpiIcons()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var sourcePath = !string.IsNullOrEmpty(_config.CustomIconPath) && File.Exists(_config.CustomIconPath)
                ? _config.CustomIconPath
                : System.IO.Path.Combine(App.Paths.IconsDir, $"{_config.Id}.png");
            if (!File.Exists(sourcePath)) return;

            var icoPath = IconHelper.EnsureIcoFile(sourcePath);
            if (string.IsNullOrEmpty(icoPath)) return;

            var (smallSize, bigSize) = NativeMethods.GetIconSizesForWindow(hwnd);
            // ICON_SMALL is consumed by Win11's taskbar tile + titlebar,
            // both of which display at the new ~24-32px size — feeding
            // them the legacy SM_CXSMICON (=16) frame would force the
            // shell to upscale to its actual slot. Pass the big-icon
            // size so the shell downscales from a sharp frame instead.
            var newSmall = NativeMethods.LoadHiconFromFile(icoPath, bigSize);
            var newBig   = NativeMethods.LoadHiconFromFile(icoPath, bigSize);

            NativeMethods.SendSetIcon(hwnd, newSmall, newBig);
            Logger.Debug($"[IconDbg] [{_config.Name}] ApplyMultiDpiIcons WM_SETICON sent: (smallApi={smallSize} bigApi={bigSize}) BOTH HICONs loaded at {bigSize}px | hSmall=0x{newSmall:X} hBig=0x{newBig:X} from '{System.IO.Path.GetFileName(icoPath)}'");

            // Free previously-owned handles only AFTER WM_SETICON commits
            // the new ones — otherwise Windows briefly paints a null icon.
            if (_hIconSmall != IntPtr.Zero) NativeMethods.DestroyIcon(_hIconSmall);
            if (_hIconBig != IntPtr.Zero)   NativeMethods.DestroyIcon(_hIconBig);

            _hIconSmall = newSmall;
            _hIconBig   = newBig;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[{_config.Name}] ApplyMultiDpiIcons failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns "&lt;path-to-ico&gt;,0" suitable for IconResource, or null
    /// if no icon is on disk yet (placeholder will be used until
    /// favicon downloads).
    /// </summary>
    private string? ResolveIconResource()
    {
        var sourcePath = !string.IsNullOrEmpty(_config.CustomIconPath) && File.Exists(_config.CustomIconPath)
            ? _config.CustomIconPath
            : System.IO.Path.Combine(App.Paths.IconsDir, $"{_config.Id}.png");

        if (!File.Exists(sourcePath)) return null;
        var ico = IconHelper.EnsureIcoFile(sourcePath);
        return string.IsNullOrEmpty(ico) ? null : $"{ico},0";
    }

    /// <summary>
    /// Registers (or refreshes) the user's custom CSS as a WebView2
    /// document-created script. The script appends a &lt;style&gt; node
    /// to document.head on every navigation, so the rules survive SPA
    /// re-renders. Safe to call repeatedly — removes the previous script
    /// id first so toggling off / editing actually takes effect.
    ///
    /// CSS body is embedded as a JSON string literal (System.Text.Json
    /// handles all the escaping) — no manual quote/backtick gymnastics.
    /// </summary>
    private async Task ApplyCustomCssAsync()
    {
        try
        {
            if (WebView?.CoreWebView2 == null) return;

            // Remove any previous registration first. WebView2 keeps the
            // script registered across reloads; without this we'd end up
            // with N stacked <style> blocks after each settings save.
            if (!string.IsNullOrEmpty(_customCssScriptId))
            {
                try { WebView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_customCssScriptId); }
                catch (Exception ex) { Logger.Warn($"[{_config.Name}] RemoveScript failed: {ex.Message}"); }
                _customCssScriptId = null;
            }

            if (!_config.CustomCssEnabled || string.IsNullOrWhiteSpace(_config.CustomCss))
                return;

            // Two-step UserCSS pre-processing:
            //  1. Strip @-moz-document wrapper (Mozilla-only @-rule, Chromium
            //     drops rules inside).
            //  2. Expand `@var color NAME "Label" VALUE` declarations into a
            //     synthesised :root { --NAME: VALUE; } block. UserCSS with
            //     @preprocessor=default relies on this for theming variables
            //     — without it, every var(--fg) in the rules resolves to
            //     nothing and the page stays unstyled even though the CSS
            //     "applies".
            var unwrapped = UnwrapUserCss(_config.CustomCss!);
            if (unwrapped.Length != _config.CustomCss!.Length)
            {
                Logger.Debug($"[{_config.Name}] Custom CSS: stripped @-moz-document wrapper ({_config.CustomCss.Length} → {unwrapped.Length} chars).");
            }
            var cssText = ExpandUserCssVars(unwrapped, out int varCount);
            if (varCount > 0)
            {
                Logger.Debug($"[{_config.Name}] Custom CSS: expanded {varCount} @var declaration(s) into a :root block.");
            }

            // Dark-theme-only gate. Wrap the whole rules block in a
            // prefers-color-scheme media query so the browser activates /
            // deactivates the styles natively when Windows switches themes
            // — no reload needed, no manual theme polling. WebView2 mirrors
            // the OS theme into prefers-color-scheme automatically.
            if (_config.CustomCssOnlyInDarkTheme)
            {
                cssText = "@media (prefers-color-scheme: dark) {\n" + cssText + "\n}";
                Logger.Debug($"[{_config.Name}] Custom CSS: wrapped in @media (prefers-color-scheme: dark).");
            }

            // JSON-encode the CSS so it lands as a valid JS string literal
            // (handles backticks, dollar signs, newlines, unicode).
            var cssLiteral = System.Text.Json.JsonSerializer.Serialize(cssText);

            // Injection strategy for SPAs (Google Translate, Slack, etc.):
            //  • Document-created script runs BEFORE page CSS loads, so an
            //    early-inserted <style> loses the cascade to later-loaded
            //    page rules with the same specificity.
            //  • Fix: keep re-anchoring our <style> as the LAST child of
            //    <head> on DOMContentLoaded, on load, AND whenever a
            //    MutationObserver sees something else get appended.
            //    This guarantees our rules sit at the end of the cascade.
            //  • A single <style data-wvh-custom='1'> is reused — we move
            //    it instead of cloning so we never end up with N copies.
            var js = $@"
(function() {{
    try {{
        // Skip iframes — translate/ads widgets don't need our styles
        // and would log noise. Only inject into the top-level document.
        if (window.top !== window.self) return;

        var css = {cssLiteral};

        function ensureLast() {{
            // At document_created the document body/head may not exist yet.
            var host = document.head || document.documentElement;
            if (!host) return;

            var s = document.querySelector('style[data-wvh-custom]');
            if (!s) {{
                s = document.createElement('style');
                s.setAttribute('data-wvh-custom', '1');
                s.textContent = css;
            }}
            if (s !== host.lastElementChild) host.appendChild(s);
        }}

        ensureLast();

        if (document.readyState === 'loading') {{
            document.addEventListener('DOMContentLoaded', ensureLast);
        }}
        window.addEventListener('load', ensureLast);

        function startObserver() {{
            var target = document.head || document.documentElement;
            if (!target) return;
            var mo = new MutationObserver(function() {{ ensureLast(); }});
            mo.observe(target, {{ childList: true }});
        }}
        if (document.head || document.documentElement) startObserver();
        else document.addEventListener('DOMContentLoaded', startObserver, {{ once: true }});
    }} catch (e) {{
        console.warn('[WVH CSS] injection error:', e);
    }}
}})();";

            _customCssScriptId = await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(js);
            Logger.Debug($"[{_config.Name}] Custom CSS injected ({cssText.Length} chars, scriptId={_customCssScriptId}).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[{_config.Name}] ApplyCustomCss failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts <c>@var color NAME "Label" VALUE</c> declarations from a
    /// UserCSS preamble and prepends a synthesised <c>:root</c> block that
    /// defines them as CSS custom properties. UserCSS with
    /// <c>@preprocessor default</c> uses these as theming variables; without
    /// expansion the rule body's <c>var(--name)</c> references resolve to
    /// nothing and the page stays unstyled.
    /// </summary>
    private static string ExpandUserCssVars(string css, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(css)) return css;

        // Match `@var color <name> "<label>" <value>` — value is the rest
        // of the line trimmed (#abc, rgb(...), white, etc.).
        // Only `color` type is handled here; number/text/select are rare
        // and would need a fuller Stylus parser.
        var rx = new System.Text.RegularExpressions.Regex(
            "@var\\s+color\\s+([\\w-]+)\\s+\"[^\"]*\"\\s+([^\\r\\n]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var seen = new HashSet<string>();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(":root {");
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(css))
        {
            var name = m.Groups[1].Value;
            var value = m.Groups[2].Value.Trim();
            if (!seen.Add(name)) continue; // de-dup repeated declarations
            sb.AppendLine($"  --{name}: {value};");
            count++;
        }
        sb.AppendLine("}");

        if (count == 0) return css;
        return sb.ToString() + "\n" + css;
    }

    /// <summary>
    /// Unwraps every <c>@-moz-document &lt;matcher&gt; { ... }</c> block by
    /// lifting the inner rules to top level. Walks brace depth manually so
    /// nested @-rules (@media inside @-moz-document, etc.) survive intact.
    /// Returns the original string if no wrapper is found.
    /// </summary>
    private static string UnwrapUserCss(string css)
    {
        if (string.IsNullOrEmpty(css)) return css;
        var sb = new System.Text.StringBuilder(css.Length);
        int i = 0;
        while (i < css.Length)
        {
            int idx = css.IndexOf("@-moz-document", i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                sb.Append(css, i, css.Length - i);
                break;
            }
            sb.Append(css, i, idx - i);

            int braceOpen = css.IndexOf('{', idx);
            if (braceOpen < 0)
            {
                sb.Append(css, idx, css.Length - idx);
                break;
            }

            int depth = 1;
            int j = braceOpen + 1;
            while (j < css.Length && depth > 0)
            {
                char c = css[j];
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) break; }
                j++;
            }
            if (j >= css.Length)
            {
                // Unbalanced — bail out, append the rest as-is.
                sb.Append(css, idx, css.Length - idx);
                break;
            }

            // Append the body (between the matching braces) — strips the
            // @-moz-document <matcher> { ... } wrapper but keeps the inner
            // rules intact.
            sb.Append(css, braceOpen + 1, j - braceOpen - 1);
            i = j + 1;
        }
        return sb.ToString();
    }

    private async Task InitializeAsync()
    {
        Logger.Info($"[{_config.Name}] InitializeAsync — start.");
        try
        {
            var controllerOptions = App.WebViewEnvironment.CreateCoreWebView2ControllerOptions();
            // Per-account session bucket. For a service with a single (legacy)
            // profile this resolves to _config.Id — the exact name used before
            // profiles existed, so existing logins keep working untouched.
            controllerOptions.ProfileName = SanitizeProfileName(_config.EffectiveProfileKey);
            controllerOptions.IsInPrivateModeEnabled = false;
            Logger.Info($"[{_config.Name}] EnsureCoreWebView2Async, profile={controllerOptions.ProfileName}");

            await WebView.EnsureCoreWebView2Async(App.WebViewEnvironment, controllerOptions);
            Logger.Info($"[{_config.Name}] CoreWebView2 ready.");

            // Virtual-host mapping — lets a local-file dashboard load as
            // https://<name>/... so fetch()/XHR to sibling JSON files
            // works (file:// blocks cross-file fetch via CORS). Applied
            // BEFORE first navigation. Both fields must be set + folder
            // must exist on disk; otherwise we skip silently — feature is
            // strictly opt-in per service.
            try
            {
                var vhost = _config.VirtualHostName?.Trim();
                var folder = _config.LocalFolderPath?.Trim();
                if (!string.IsNullOrEmpty(vhost) && !string.IsNullOrEmpty(folder))
                {
                    if (System.IO.Directory.Exists(folder))
                    {
                        WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            vhost,
                            folder,
                            CoreWebView2HostResourceAccessKind.Allow);
                        Logger.Info($"[{_config.Name}] Virtual host mapped: https://{vhost}/ → {folder}");
                    }
                    else
                    {
                        Logger.Warn($"[{_config.Name}] Virtual host mapping skipped — folder does not exist: {folder}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[{_config.Name}] SetVirtualHostNameToFolderMapping failed", ex);
            }

            var ua = _config.EffectiveUserAgent;
            if (!string.IsNullOrEmpty(ua))
            {
                WebView.CoreWebView2.Settings.UserAgent = ua;
            }

            // Custom CSS injection — userstyles.world / Stylus snippets.
            // Registers on document-create so every navigation in this
            // service window gets the user's CSS rules; live-updated by
            // ApplyConfigUpdate when settings save.
            await ApplyCustomCssAsync();

            // Notifications — two layers, plus diagnostic logging.
            //
            // 1. Profile.SetPermissionStateAsync proactively writes Allow
            //    for the service origin into the WebView2 profile. This is
            //    the important one: in earlier sessions (before any handler
            //    existed) WebView2 silently denied the request, and pages
            //    like Slack cache "denied" client-side and never re-prompt.
            //    Overwriting profile state takes effect immediately and
            //    persists across launches.
            //
            // 2. PermissionRequested still grants any *new* request that
            //    arrives at runtime (e.g. cross-origin iframes), and logs
            //    each request so we can see what the page is asking for.
            //
            // 3. NotificationReceived is logged for diagnostics. We don't
            //    set Handled=true, so WebView2 still shows its own native
            //    Windows toast — the log just lets us confirm the page
            //    actually fires Notification() on incoming messages.
            try
            {
                var origin = TryGetOrigin(_config.Url);
                if (!string.IsNullOrEmpty(origin) && WebView.CoreWebView2.Profile != null)
                {
                    await WebView.CoreWebView2.Profile.SetPermissionStateAsync(
                        CoreWebView2PermissionKind.Notifications,
                        origin,
                        CoreWebView2PermissionState.Allow);
                    Logger.Info($"[{_config.Name}] Pre-granted Notifications for {origin}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[{_config.Name}] SetPermissionStateAsync", ex);
            }

            WebView.CoreWebView2.PermissionRequested += (_, args) =>
            {
                Logger.Info($"[{_config.Name}] PermissionRequested: {args.PermissionKind} for {args.Uri}");
                if (args.PermissionKind == CoreWebView2PermissionKind.Notifications)
                {
                    args.State = CoreWebView2PermissionState.Allow;
                    args.Handled = true;
                }
            };

            WebView.CoreWebView2.NotificationReceived += (_, e) =>
            {
                try
                {
                    Logger.Info(
                        $"[{_config.Name}] NotificationReceived: " +
                        $"title='{e.Notification?.Title}' body='{e.Notification?.Body}' " +
                        $"sender='{e.SenderOrigin}'");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[{_config.Name}] NotificationReceived handler", ex);
                }
            };

            // Restore persisted zoom + listen for further changes.
            try
            {
                if (_config.ZoomFactor > 0 && Math.Abs(_config.ZoomFactor - 1.0) > 0.001)
                {
                    WebView.ZoomFactor = _config.ZoomFactor;
                    Logger.Info($"[{_config.Name}] Restored zoom = {_config.ZoomFactor:F2}");
                }
                WebView.ZoomFactorChanged += OnZoomFactorChanged;
            }
            catch (Exception ex)
            {
                Logger.Error($"[{_config.Name}] Zoom setup", ex);
            }

            // Title-bar Back/Forward buttons follow the embedded history.
            // HistoryChanged fires on every navigation that mutates the
            // back/forward stack, so we just rebind both IsEnabled flags.
            WebView.CoreWebView2.HistoryChanged += (_, _) =>
            {
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        BackButton.IsEnabled = WebView.CoreWebView2.CanGoBack;
                        ForwardButton.IsEnabled = WebView.CoreWebView2.CanGoForward;
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"[{_config.Name}] HistoryChanged handler", ex);
                }
            };

            WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            WebView.CoreWebView2.DocumentTitleChanged += (s, e) =>
            {
                try
                {
                    var docTitle = WebView.CoreWebView2.DocumentTitle ?? "";
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            Title = ComposeTitle(docTitle);
                            if (_config.UnreadBadge == UnreadBadgeMode.TitleRegex)
                            {
                                UpdateUnreadCount(ParseUnreadFromTitle(docTitle));
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[{_config.Name}] DocumentTitleChanged dispatcher", ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"[{_config.Name}] DocumentTitleChanged handler", ex);
                }
            };

            var initialUrl = _pendingNavigationUrl ?? _config.Url;
            _pendingNavigationUrl = null;
            Logger.Info($"[{_config.Name}] Navigating to {initialUrl}");
            WebView.CoreWebView2.Navigate(initialUrl);

            SetupTrayIcon();

            // Kick off favicon load. Once it lands we replace the placeholder
            // base icon and re-render any active badge on top of it.
            _ = Task.Run(async () =>
            {
                try
                {
                    var iconPath = !string.IsNullOrEmpty(_config.CustomIconPath)
                        ? _config.CustomIconPath
                        : await App.Favicon.GetIconAsync(_config.Id, _config.Url, _config.Name);

                    if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
                        return;

                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            LoadBaseIcon(iconPath);
                            ApplyUnreadVisuals();
                            ApplyTaskbarIdentity();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[{_config.Name}] Favicon UI apply", ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"[{_config.Name}] Favicon download task", ex);
                }
            });

            LoadBaseIcon(null);
            ApplyUnreadVisuals();

            LoadingOverlay.Visibility = Visibility.Collapsed;

            if (_hideAfterInit)
            {
                _hideAfterInit = false;
                Logger.Info($"[{_config.Name}] Init complete — hiding to tray (StartMinimized).");
                Logger.Debug($"[{_config.Name}] Pre-hide state — {TrayState()}");
                HideToTray();
                UncloakAfterHiddenInit();
                // Re-enable activation for subsequent ShowFromTray calls.
                ShowActivated = true;
            }

            Logger.Info($"[{_config.Name}] Window initialized — {TrayState()}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[{_config.Name}] InitializeAsync failed", ex);
            MessageBox.Show(
                $"Failed to initialize WebView2 for \"{_config.Name}\":\n\n{ex.Message}\n\n" +
                $"See logs in:\n{Logger.LogsDir}",
                "WebViewHub",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private static string? TryGetOrigin(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? $"{u.Scheme}://{u.Host}"
            : null;
    }

    private static string SanitizeProfileName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                sb.Append(c);
            else
                sb.Append('_');
        }
        var result = sb.ToString();
        if (string.IsNullOrEmpty(result)) result = "default";
        if (result.Length > 64) result = result[..64];
        return result;
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            // Use Icon (System.Drawing.Icon) directly — H.NotifyIcon's
            // IconSource→Icon converter NREs on stream-backed BitmapImage
            // (our letter/badge bitmaps don't have a UriSource). Set the
            // Icon property directly and feed Shell_NotifyIcon with a
            // properly-sized HICON via our existing BitmapToIcon helper.
            Icon = IconHelper.GenerateLetterIcon(_config.Name),
            ToolTipText = _config.Name.Length > 63 ? _config.Name[..63] : _config.Name,
            ContextMenu = BuildTrayMenu(),
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };

        _trayIcon.TrayLeftMouseUp += (_, _) =>
        {
            if (IsVisible && IsActive) HideToTray();
            else ShowFromTray();
        };

        // Rebuild menu fresh each right-click so the Show/Hide label
        // and "Close {name}" caption pick up the current state.
        _trayIcon.TrayRightMouseDown += (_, _) =>
        {
            _trayIcon.ContextMenu = BuildTrayMenu();
        };

        _trayIcon.ForceCreate();
    }

    /// <summary>
    /// Built fresh each right-click so dynamic state (current Name in the
    /// "Close {name}" label, visibility wording) stays in sync without
    /// listening to config-change events.
    /// </summary>
    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = TrayContextMenu.Create();
        var showHideLabel = (IsVisible && IsActive) ? "Hide" : "Show";
        menu.AddItem(showHideLabel,
            (IsVisible && IsActive) ? SymbolRegular.EyeOff24 : SymbolRegular.Eye24,
            (_, _) => { if (IsVisible && IsActive) HideToTray(); else ShowFromTray(); });
        menu.AddItem("Reload", SymbolRegular.ArrowClockwise24,
            (_, _) => { try { WebView.CoreWebView2?.Reload(); } catch { } });
        menu.AddSeparator();
        menu.AddItem("Settings…", SymbolRegular.Settings24,
            (_, _) => App.Windows.OpenServiceSettings(_config));
        menu.AddItem("Open hub…", SymbolRegular.Apps24,
            (_, _) => App.Windows.ShowHub());
        menu.AddSeparator();
        menu.AddItem($"Close {_config.Name}", SymbolRegular.Dismiss24,
            (_, _) => ForceClose());
        menu.AddItem("Quit WebViewHub", SymbolRegular.Power24,
            (_, _) => App.Windows.QuitApp());
        return menu;
    }

    private void LoadBaseIcon(string? sourcePath)
    {
        _baseIconBitmap?.Dispose();
        // Load at 256px so BadgeRenderer has plenty of source pixels to
        // downscale into the actual tray slot (16/20/24/32 depending on
        // DPI). At 64px the rim of the original squircle gets compressed
        // 4× when re-rendering for tray, producing the soft-alpha edge
        // that visually shrinks the icon.
        _baseIconBitmap = IconHelper.LoadOrGenerateBitmap(sourcePath, _config.Name, 256);
        _iconSourcePath = sourcePath;
    }

    private int ParseUnreadFromTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return 0;
        try
        {
            var pattern = _config.EffectiveUnreadRegex;
            var match = Regex.Match(title, pattern);
            if (!match.Success) return 0;

            // Use the first numeric group; otherwise fall back to whole match.
            string? candidate = null;
            for (int i = 1; i < match.Groups.Count; i++)
            {
                if (int.TryParse(match.Groups[i].Value, out _))
                {
                    candidate = match.Groups[i].Value;
                    break;
                }
            }
            candidate ??= match.Value;
            return int.TryParse(candidate, out var n) && n > 0 ? n : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void UpdateUnreadCount(int count)
    {
        if (_unreadCount == count) return;
        _unreadCount = count;
        ApplyUnreadVisuals();
    }

    /// <summary>
    /// Re-paints tray icon, window icon, and taskbar overlay from
    /// _baseIconBitmap + _unreadCount. Called on count change, on icon
    /// change, and on config change.
    /// </summary>
    private void ApplyUnreadVisuals()
    {
        if (_baseIconBitmap == null) return;

        // Tray icon — full base + corner badge baked in. NotifyIcon has no
        // native overlay API, so the badge has to be part of the bitmap.
        // We compose at the DPI-aware tray slot size and hand
        // H.NotifyIcon a System.Drawing.Icon directly (its IconSource
        // converter crashes on stream-backed BitmapImage).
        if (_trayIcon != null)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int traySize = NativeMethods.GetTrayIconSize(hwnd);
            if (traySize <= 0) traySize = 32;

            using var withBadge = BadgeRenderer.Render(_baseIconBitmap, _unreadCount, traySize);
            var newIcon = IconHelper.BitmapToIcon(withBadge, traySize);
            var old = _trayIcon.Icon;
            _trayIcon.Icon = newIcon;
            old?.Dispose();
            Logger.Debug($"[IconDbg] [{_config.Name}] ApplyUnreadVisuals: _trayIcon.Icon assigned ({newIcon.Width}×{newIcon.Height}, unread={_unreadCount}, traySize={traySize})");
        }
        else
        {
            Logger.Warn($"[IconDbg] [{_config.Name}] ApplyUnreadVisuals: _trayIcon is null — icon not assigned");
        }

        // Window icon — load PNG straight from disk via WPF. Going through
        // System.Drawing + Graphics.DrawImage shifts colours on partially
        // transparent edges (gamma-incorrect compositing onto an empty
        // ARGB canvas), which showed up as muddy taskbar icons. The unread
        // badge for the taskbar is supplied via TaskbarItemInfo.Overlay
        // below — Windows-native, no GDI+ needed. Trade-off: the badge no
        // longer overlays the Alt+Tab thumbnail; it stays visible on the
        // taskbar tile and in the tray.
        // Pre-scale to 256px so Windows' HICON conversion downscales
        // 256→32/48 instead of 1024→32/48 (much less aliasing).
        Icon = (string.IsNullOrEmpty(_iconSourcePath)
                   ? null
                   : IconHelper.LoadWpfImageScaled(_iconSourcePath, 256)
                     ?? (System.Windows.Media.ImageSource?)IconHelper.LoadWpfImage(_iconSourcePath))
               ?? IconHelper.GenerateLetterImage(_config.Name);

        // Taskbar overlay — Windows-native overlay, just the badge circle.
        if (TaskbarItemInfo != null)
        {
            if (_unreadCount <= 0)
            {
                TaskbarItemInfo.Overlay = null;
                TaskbarItemInfo.Description = _config.Name;
            }
            else
            {
                using var overlayBmp = BadgeRenderer.RenderOverlay(_unreadCount);
                TaskbarItemInfo.Overlay = BadgeRenderer.ToBitmapImage(overlayBmp);
                TaskbarItemInfo.Description = $"{_config.Name} — {_unreadCount} unread";
            }
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Logger.Debug($"[{_config.Name}] OnStateChanged — {TrayState()}");
        if (WindowState == WindowState.Minimized && _config.MinimizeToTray)
        {
            HideToTray();
        }
    }

    /// <summary>
    /// Spawns an in-process <see cref="PopupWindow"/> child WebView2 for
    /// any <c>window.open()</c> from the page — instead of routing to
    /// the system browser. The popup shares this service's WebView2
    /// ProfileName so cookies set during OAuth (Sign in with Google,
    /// Microsoft, GitHub, etc.) are immediately visible to the parent
    /// page on its next request, completing the auth callback natively.
    /// On any failure (sandbox limits, CoreWebView2 init crash) we fall
    /// back to the previous system-browser behavior so the user at
    /// least sees the URL.
    /// </summary>
    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            // Route only OAuth-style flows to an in-process PopupWindow so
            // the auth callback completes in the same WebView2 profile and
            // the parent page sees the session cookie. Everything else
            // (target="_blank" article links, "open in new tab" gestures
            // on regular content) goes to the system browser — matches
            // user expectation of "external links open externally".
            if (IsLikelyAuthFlow(e.Uri, e.WindowFeatures))
            {
                var (w, h, l, t) = PopupWindow.ReadFeatures(e.WindowFeatures);
                // Must be the ACTIVE profile's bucket, not the service default —
                // an OAuth popup that authenticates into a different session
                // than the parent page leaves the user stuck at a login wall.
                var popup = new PopupWindow(SanitizeProfileName(_config.EffectiveProfileKey), _config.Name, w, h, l, t)
                {
                    Owner = this
                };
                popup.Show();
                var core = await popup.CoreReady.Task;
                e.NewWindow = core;
                e.Handled = true;
                Logger.Debug($"[{_config.Name}] NewWindowRequested → AUTH popup spawned for {e.Uri} (userInitiated={e.IsUserInitiated}, hasSize={e.WindowFeatures?.HasSize})");
            }
            else
            {
                e.Handled = true;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri,
                    UseShellExecute = true
                });
                Logger.Debug($"[{_config.Name}] NewWindowRequested → external browser for {e.Uri} (userInitiated={e.IsUserInitiated})");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[{_config.Name}] NewWindowRequested handler failed: {ex.Message} — falling back to system browser");
            try
            {
                e.Handled = true;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri,
                    UseShellExecute = true
                });
            }
            catch (Exception ex2)
            {
                Logger.Error($"[{_config.Name}] NewWindowRequested fallback to browser also failed", ex2);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Heuristic classification of a <c>window.open()</c> request as an
    /// authentication popup vs. a generic external link. We deliberately
    /// err on the side of <em>routing to popup</em> when ambiguous —
    /// missing an OAuth flow means a broken login (silent and severe);
    /// over-popping a non-OAuth link just means the user gets it in our
    /// window instead of their browser (annoying but reversible: they can
    /// close + manually open in browser).
    ///
    /// Three positive signals, in order of confidence:
    /// <list type="number">
    /// <item><c>WindowFeatures.HasSize == true</c> — the page called
    ///   <c>window.open(url, name, "width=X,height=Y")</c>. OAuth providers
    ///   universally do this for their consent windows; plain
    ///   <c>&lt;a target="_blank"&gt;</c> links never set features.</item>
    /// <item>Host matches a known OAuth provider (Google, Microsoft,
    ///   Apple, GitHub, Slack, Discord, etc.) — covers cases where the
    ///   site stripped features for some reason but the destination is
    ///   clearly an auth domain.</item>
    /// <item>URL path matches a canonical OAuth path
    ///   (<c>/oauth/</c>, <c>/oauth2/</c>, <c>/signin</c>, <c>/login</c>,
    ///   <c>/authorize</c>, <c>/sso/</c>, <c>/openid</c>) — catches
    ///   self-hosted SSO flows.</item>
    /// </list>
    /// </summary>
    private static bool IsLikelyAuthFlow(string? uri, CoreWebView2WindowFeatures? features)
    {
        // Signal 1: popup-style window features = definitely OAuth/popup intent.
        if (features?.HasSize == true) return true;

        if (string.IsNullOrWhiteSpace(uri)) return false;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        var host = parsed.Host.ToLowerInvariant();
        var path = parsed.AbsolutePath.ToLowerInvariant();

        // Signal 2: known auth-provider hosts. Match both exact and
        // subdomain (login.foo.com → matches "foo.com" entry).
        string[] authHosts = {
            "accounts.google.com", "oauth2.googleapis.com",
            "login.microsoftonline.com", "login.live.com", "login.windows.net",
            "appleid.apple.com",
            "github.com",                  // /login/oauth/* lives here
            "slack.com",                   // /openid/* and oauth/v2/*
            "discord.com",                 // /oauth2/*
            "facebook.com",                // /dialog/oauth, /v*/dialog/oauth
            "linkedin.com",                // /oauth/v2/*
            "twitter.com", "x.com",        // /i/oauth2/*
            "auth0.com", "okta.com", "onelogin.com",
            "yandex.ru", "passport.yandex.ru",
            "vk.com",                      // /authorize, /oauth/authorize
            "ya.ru"
        };
        foreach (var h in authHosts)
        {
            if (host == h || host.EndsWith("." + h, StringComparison.Ordinal))
                return true;
        }

        // Signal 3: canonical OAuth/SSO path patterns — catches self-hosted
        // SSO (e.g. internal corp identity providers) and OAuth endpoints
        // hosted on the service's primary domain (Notion, Linear, etc.
        // run their own /oauth/authorize routes).
        string[] authPaths = {
            "/oauth/", "/oauth2/", "/o/oauth2/",
            "/signin", "/sign-in", "/sign_in",
            "/login", "/log-in", "/log_in",
            "/authorize", "/authenticate",
            "/sso/", "/saml/", "/openid",
            "/consent"
        };
        foreach (var p in authPaths)
        {
            if (path.Contains(p, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Updates the in-memory zoom value on every change and schedules a
    /// debounced disk save 1s after the last change. OnClosing also
    /// SaveSync's, so even before the timer fires the value is captured
    /// at window close.
    /// </summary>
    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        try
        {
            var newZoom = WebView.ZoomFactor;
            if (Math.Abs(newZoom - _config.ZoomFactor) < 0.001) return;

            _config.ZoomFactor = newZoom;

            if (_zoomSaveTimer == null)
            {
                _zoomSaveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _zoomSaveTimer.Tick += (_, _) =>
                {
                    _zoomSaveTimer.Stop();
                    try { App.Config.SaveSync(); }
                    catch (Exception ex) { Logger.Error("Zoom save tick", ex); }
                };
            }
            _zoomSaveTimer.Stop();
            _zoomSaveTimer.Start();
        }
        catch (Exception ex)
        {
            Logger.Error($"[{_config.Name}] OnZoomFactorChanged", ex);
        }
    }

    /// <summary>
    /// When the user actually focuses the window the badge typically goes
    /// away on the site's own — but our title-regex parser only updates
    /// on title change, not on focus. So we proactively zero the count
    /// here and let the next title event re-set it if it's still there.
    /// </summary>
    private void OnActivatedClearBadge(object? sender, EventArgs e)
    {
        if (_config.UnreadBadge == UnreadBadgeMode.Off) return;
        if (_unreadCount > 0) UpdateUnreadCount(0);
    }

    // Usage-tracking handlers — stubs for now. The Activated/Deactivated
    // wire-up + App.Usage call were added by an external edit (likely a
    // future App.Usage analytics path). Empty implementations keep the
    // build green until App.Usage is implemented.
    private void OnActivatedTrackUsage(object? sender, EventArgs e) { }
    private void OnDeactivatedTrackUsage(object? sender, EventArgs e) { }

    /// <summary>
    /// Peek-window behavior: when the user clicks back to another app,
    /// hide this window to the tray. WebView2 keeps running so the next
    /// hotkey press restores it instantly. Hide is dispatched async so we
    /// don't fight WPF's own focus-tracking inside the Deactivated event.
    /// </summary>
    private void OnDeactivatedAutoHide(object? sender, EventArgs e)
    {
        if (!_config.CloseOnFocusLost) return;
        if (_forceClosing || !IsVisible) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_forceClosing || !IsVisible || IsActive) return;
            HideToTray();
        }), DispatcherPriority.Background);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // RestoreBounds gives the bounds the window had when it was last
        // in Normal state — so even if the user closes while minimized
        // (common for translator services that live in the tray) we still
        // persist a sensible size/position. WPF returns Empty when the
        // window has never been Normal, in which case Width/Height stay
        // at their config defaults.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (_config.RememberWindowState && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
        {
            // OpenCentered: persist size but not position, and clear any
            // stale Left/Top so re-reading the config doesn't accidentally
            // resurface an old saved location.
            if (_config.OpenCentered)
            {
                _config.WindowLeft = null;
                _config.WindowTop = null;
            }
            else
            {
                _config.WindowLeft = bounds.Left;
                _config.WindowTop = bounds.Top;
            }
            _config.WindowWidth = bounds.Width;
            _config.WindowHeight = bounds.Height;
            // Synchronous — must complete before process shutdown can
            // interrupt it. The previous fire-and-forget had a race that
            // ate config.json when two windows closed simultaneously.
            App.Config.SaveSync();
        }

        if (_forceClosing || !_config.CloseToTray)
        {
            ApplicationThemeManager.Changed -= OnAppThemeChanged;
            if (_trayIcon != null)
            {
                // H.NotifyIcon.Dispose unregisters from Shell_NotifyIcon
                // and releases the internally-managed HICON.
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _baseIconBitmap?.Dispose();
            _baseIconBitmap = null;
            _zoomSaveTimer?.Stop();
            _zoomSaveTimer = null;
            // Release WM_SETICON handles we own (WPF Window.Icon's handle
            // is managed by WPF — we don't touch it).
            if (_hIconSmall != IntPtr.Zero) { NativeMethods.DestroyIcon(_hIconSmall); _hIconSmall = IntPtr.Zero; }
            if (_hIconBig != IntPtr.Zero)   { NativeMethods.DestroyIcon(_hIconBig);   _hIconBig   = IntPtr.Zero; }
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    /// <summary>
    /// Navigates the embedded WebView2 to a fresh URL. If WebView2 hasn't
    /// finished initializing yet, the URL is queued and consumed once
    /// <see cref="InitializeAsync"/> finishes. Used by the translator flow
    /// to refresh the page with new clipboard text on every trigger.
    /// </summary>
    public void NavigateTo(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            if (WebView?.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Navigate(url);
            }
            else
            {
                _pendingNavigationUrl = url;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[{_config.Name}] NavigateTo failed", ex);
        }
    }

    public void RequestHideAfterInit()
    {
        _hideAfterInit = true;
        Logger.Debug($"[{_config.Name}] RequestHideAfterInit — flag set.");
    }

    /// <summary>
    /// Marks the window to be DWM-cloaked from its first paint, so the
    /// WebView2 initialization (which requires a real Show()) never
    /// surfaces a visible window. The cloak is applied in
    /// <see cref="OnSourceInitialized"/> — before WPF posts WM_SHOWWINDOW —
    /// and removed by <see cref="UncloakAfterHiddenInit"/> after the
    /// post-init Hide() so subsequent <see cref="ShowFromTray"/> calls
    /// display normally.
    ///
    /// Call BEFORE <see cref="Window.Show"/>.
    /// </summary>
    public void PrepareHiddenStart()
    {
        _hiddenStartParked = true;
        ShowActivated = false;
        Logger.Debug($"[{_config.Name}] PrepareHiddenStart — cloak armed, ShowActivated=false.");
    }

    /// <summary>
    /// Removes the DWM cloak that <see cref="PrepareHiddenStart"/> armed.
    /// Called right after the post-init Hide() — the window is no longer
    /// visible (Hide() handled that), but the cloak attribute must be
    /// cleared so future ShowFromTray() renders normally.
    /// </summary>
    private void UncloakAfterHiddenInit()
    {
        if (!_hiddenStartParked) return;
        _hiddenStartParked = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) NativeMethods.SetCloak(hwnd, false);
        Logger.Debug($"[{_config.Name}] UncloakAfterHiddenInit — cloak removed.");
    }

    /// <summary>Compact state snapshot for diagnostic logging of the
    /// StartMinimized + tray show/hide lifecycle.</summary>
    private string TrayState()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return $"IsVisible={IsVisible} WindowState={WindowState} IsActive={IsActive} " +
               $"ShowInTaskbar={ShowInTaskbar} ShowActivated={ShowActivated} " +
               $"Left={Left:F0} Top={Top:F0} W={Width:F0} H={Height:F0} HWND=0x{hwnd.ToInt64():X}";
    }

    public void HideToTray()
    {
        Logger.Debug($"[{_config.Name}] HideToTray BEFORE — {TrayState()}");
        Hide();
        Logger.Debug($"[{_config.Name}] HideToTray AFTER  — {TrayState()}");
    }

    public void ShowFromTray()
    {
        Logger.Debug($"[{_config.Name}] ShowFromTray ENTER — {TrayState()} OpenCentered={_config.OpenCentered}");
        if (_config.OpenCentered)
        {
            CenterOnPrimaryScreen();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
        // Win32 fallback: WPF Show + Activate can no-op when the window is
        // already considered "active" by Win32 (e.g. after a hidden start
        // where the window was shown then immediately hidden). Drive Z-order
        // directly so the window reliably surfaces on the first tray click.
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ForceShowAndForeground(hwnd);
        Focus();
        Logger.Debug($"[{_config.Name}] ShowFromTray EXIT  — {TrayState()}");
    }

    /// <summary>
    /// Snaps the window to the work-area center of the primary monitor.
    /// Triggered on every ShowFromTray when <see cref="ServiceConfig.OpenCentered"/>
    /// is on, so any manual drag the user did during the last visible
    /// session is discarded.
    /// </summary>
    private void CenterOnPrimaryScreen()
    {
        var wa = System.Windows.SystemParameters.WorkArea;
        // ActualWidth/Height are 0 when the window has never been visibly
        // laid out — fall back to the bound Width/Height in that case.
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        Left = wa.Left + Math.Max(0, (wa.Width - w) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - h) / 2);
        Logger.Debug($"[{_config.Name}] CenterOnPrimaryScreen — moved to ({Left:F0},{Top:F0}) using size {w:F0}x{h:F0}");
    }

    public void ForceClose()
    {
        _forceClosing = true;
        Close();
    }

    private void OnAppThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        Dispatcher.BeginInvoke(new Action(UpdateTitleBarIconColors));
    }

    private void UpdateTitleBarIconColors()
    {
        var isDark = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark;
        var brush = new System.Windows.Media.SolidColorBrush(isDark
            ? System.Windows.Media.Colors.White
            : System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
        brush.Freeze();

        SettingsIcon.Foreground = brush;
        BackIcon.Foreground = brush;
        ForwardIcon.Foreground = brush;

        // SymbolIcon.Foreground doesn't propagate to the inner TextBlock
        // that renders the glyph — force it on each inner TextBlock directly.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyForegroundToInnerTextBlock(SettingsIcon, brush);
            ApplyForegroundToInnerTextBlock(BackIcon, brush);
            ApplyForegroundToInnerTextBlock(ForwardIcon, brush);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void ApplyForegroundToInnerTextBlock(System.Windows.DependencyObject parent, System.Windows.Media.Brush brush)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.TextBlock tb)
            {
                tb.Foreground = brush;
                return;
            }
            ApplyForegroundToInnerTextBlock(child, brush);
        }
    }

    /// <summary>
    /// Fills the "Profiles" submenu: one row per account with a checkmark on
    /// the active one, then "Add profile…". Always shown even for a single
    /// profile — otherwise there is nowhere to discover "Add profile…".
    /// </summary>
    private void BuildProfilesSubmenu(System.Windows.Controls.ContextMenu menu)
    {
        _config.EnsureProfiles();

        var profiles = menu.AddSubmenu("Profiles", SymbolRegular.People24);

        foreach (var profile in _config.Profiles)
        {
            var isActive = profile.Id == _config.ActiveProfileId;
            var id = profile.Id;   // capture by value — the loop variable is reused
            var label = string.IsNullOrWhiteSpace(profile.Name) ? "(unnamed)" : profile.Name;

            profiles.AddItem(label,
                isActive ? SymbolRegular.Checkmark24 : null,
                (_, _) => App.Windows.SwitchProfile(_config.Id, id));
        }

        profiles.AddSeparator();
        // Dispatched rather than run inline: these open a modal window, and
        // doing that from inside a ContextMenu click handler blocks while the
        // menu is still tearing down.
        profiles.AddItem("Add profile…", SymbolRegular.Add24,
            (_, _) => Dispatcher.BeginInvoke(new Action(AddProfile)));
        profiles.AddItem("Rename current…", SymbolRegular.Rename24,
            (_, _) => Dispatcher.BeginInvoke(new Action(RenameProfile)));

        // Deleting the last profile would leave the service with no session to
        // open, so the entry only exists once there is a fallback.
        if (_config.Profiles.Count > 1)
        {
            profiles.AddItem("Delete current…", SymbolRegular.Delete24,
                (_, _) => Dispatcher.BeginInvoke(new Action(DeleteProfile)));
        }
    }

    /// <summary>
    /// Renames the active profile. Deliberately does NOT touch ProfileKey —
    /// the session bucket must stay put, or renaming would silently sign the
    /// user out. No restart needed; only the title changes.
    /// </summary>
    private void RenameProfile()
    {
        var current = _config.ActiveProfile;

        var name = PromptWindow.AskForText(
            this,
            "Rename profile",
            "Renaming only changes the label. The session stays signed in.",
            "Rename",
            current.Name);

        if (name == null || name.Length == 0 || name == current.Name) return;

        current.Name = name;
        App.Config.SaveSync();
        Title = ComposeTitle(WebView.CoreWebView2?.DocumentTitle);
        Logger.Info($"[{_config.Name}] Renamed profile to '{name}'.");
    }

    /// <summary>
    /// Removes the active profile from the service and switches to whatever
    /// remains. The on-disk session folder is intentionally left behind: it is
    /// locked by the running browser process, and orphaned buckets are
    /// reclaimed by the separate storage-cleanup pass.
    /// </summary>
    private void DeleteProfile()
    {
        if (_config.Profiles.Count <= 1) return;

        var current = _config.ActiveProfile;

        var confirmed = PromptWindow.Confirm(
            this,
            "Delete profile",
            $"Delete \"{current.Name}\" from {_config.Name}?\n\n" +
            "This removes the account from the profile list and signs you out of it here. " +
            "Other profiles are unaffected.",
            "Delete",
            danger: true);

        if (!confirmed) return;

        _config.Profiles.Remove(current);
        var fallback = _config.Profiles[0];
        _config.ActiveProfileId = null;   // force SwitchProfile to see a real change
        App.Config.SaveSync();
        Logger.Info($"[{_config.Name}] Deleted profile '{current.Name}' (bucket {current.ProfileKey} left on disk).");

        App.Windows.SwitchProfile(_config.Id, fallback.Id);
    }

    /// <summary>
    /// Prompts for a name, creates a fresh (logged-out) session bucket and
    /// switches to it. The restart lands the user on the service's login page
    /// in the new profile.
    /// </summary>
    private void AddProfile()
    {
        var entered = PromptWindow.AskForText(
            this,
            "Add profile",
            $"A new profile starts signed out — you'll log into {_config.Name} again in it. " +
            "Existing profiles are untouched.",
            "Add");

        if (entered == null) return;

        var name = entered.Length == 0 ? $"Profile {_config.Profiles.Count + 1}" : entered;

        var profile = new ServiceProfile
        {
            Name = name,
            ProfileKey = _config.NewProfileKey(),
        };
        _config.Profiles.Add(profile);
        Logger.Info($"[{_config.Name}] Added profile '{name}' (bucket={profile.ProfileKey}).");

        App.Windows.SwitchProfile(_config.Id, profile.Id);
    }

    /// <summary>
    /// Builds the window title, appending the active profile name once a
    /// service has more than one account. Without it, six identical-looking
    /// windows give the user no way to tell which account they are in.
    /// </summary>
    private string ComposeTitle(string? docTitle)
    {
        var baseTitle = string.IsNullOrEmpty(docTitle) ? _config.Name : $"{docTitle} — {_config.Name}";
        if (_config.Profiles.Count <= 1) return baseTitle;

        var profileName = _config.ActiveProfile.Name;
        return string.IsNullOrWhiteSpace(profileName) ? baseTitle : $"{baseTitle} · {profileName}";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // The gear is a menu button now, not a direct shortcut: it drops a
        // small Fluent menu (Settings / Restart / Quit) anchored under the
        // button. Same AddItem/AddSeparator helpers as the tray menu, so the
        // styling (acrylic, icons) matches. StaysOpen=false dismisses on an
        // outside click.
        var menu = new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            PlacementTarget = (UIElement)sender,
            StaysOpen = false,
        };
        menu.AddItem("Settings…", SymbolRegular.Settings24,
            (_, _) => App.Windows.OpenServiceSettings(_config));

        BuildProfilesSubmenu(menu);

        menu.AddItem("Restart", SymbolRegular.ArrowClockwise24,
            (_, _) => App.Windows.RestartService(_config.Id));
        menu.AddSeparator();
        // "Quit" closes only this service window (real close, even under
        // CloseToTray). The app keeps running if other windows / hotkeys do.
        menu.AddItem("Quit", SymbolRegular.Dismiss24,
            (_, _) => ForceClose());
        menu.IsOpen = true;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (WebView.CoreWebView2?.CanGoBack == true) WebView.CoreWebView2.GoBack();
        }
        catch (Exception ex) { Logger.Error($"[{_config.Name}] GoBack", ex); }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (WebView.CoreWebView2?.CanGoForward == true) WebView.CoreWebView2.GoForward();
        }
        catch (Exception ex) { Logger.Error($"[{_config.Name}] GoForward", ex); }
    }
}
