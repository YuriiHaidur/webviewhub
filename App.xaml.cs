using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Appearance;
using WebViewHub.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace WebViewHub;

public partial class App : Application
{
    public static AppPaths Paths { get; private set; } = null!;
    public static ConfigManager Config { get; private set; } = null!;
    public static FaviconService Favicon { get; private set; } = null!;
    public new static WindowManager Windows { get; private set; } = null!;
    public static HotkeyManager Hotkeys { get; private set; } = null!;
    public static DoubleCtrlCDetector DoubleCtrlC { get; private set; } = null!;
    public static CoreWebView2Environment WebViewEnvironment { get; private set; } = null!;
    /// <summary>Append-only usage event log — service open/focus/close
    /// events feeding the Stats window. See <see cref="UsageTracker"/>.</summary>
    public static UsageTracker Usage { get; private set; } = null!;
    /// <summary>Periodic mem/CPU/threads/WV2-helpers sampler. Writes
    /// CSV to <c>Data/perf/perf-yyyyMMdd.csv</c> + one [PERF] line to
    /// the main log per snap. Use for tracking improvements across
    /// changes (e.g. hibernation savings).</summary>
    public static PerfMonitor Perf { get; private set; } = null!;

    /// <summary>Active double-Ctrl+C subscriptions, keyed by service id,
    /// so we can revoke when the user toggles the option off or deletes
    /// a translator service.</summary>
    private static readonly Dictionary<string, IDisposable> _doubleCtrlCSubs = new();

    /// <summary>
    /// True when this startup detected that the .exe path differs from the
    /// path stored in config from the previous run — i.e. the user moved
    /// the install. The Hub uses this to surface a one-shot confirmation
    /// that shortcuts have been re-pointed at the new location.
    /// </summary>
    public static bool WasRelocated { get; private set; }
    public static string? PreviousExePath { get; private set; }

    private SingleInstanceManager? _singleInstance;

    /// <summary>
    /// Pipe-args (--service=...) that arrived while <see cref="Windows"/>
    /// was still null during OnStartup. We can't process them until the
    /// WindowManager + Config are ready, so they queue here and replay
    /// once init reaches that point. Without this queue, concurrent
    /// autostart entries lose any service whose pipe message arrived
    /// before the WindowManager assignment.
    /// </summary>
    private readonly List<string[]> _pendingPipeArgs = new();
    private readonly object _pendingPipeArgsLock = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Paths = AppPaths.Resolve();

            // Logger first — so everything below can use it.
            Logger.Init(Paths.DataDir);
            HookGlobalExceptionHandlers();
            Logger.Info($"Data dir: {Paths.DataDir} ({(Paths.IsPortable ? "portable" : "appdata")})");

            // Best-effort cleanup of the legacy 'webviewhub://' protocol
            // registration from previous builds — we now ship Raycast Script
            // Commands instead, so the URL handler is dead code on disk.
            CleanupLegacyProtocolRegistration();

            _singleInstance = new SingleInstanceManager("WebViewHub.SingleInstance.v1");
            if (!_singleInstance.TryAcquire())
            {
                Logger.Info("Another instance is running — forwarding args and exiting.");
                _singleInstance.SendCommandToRunningInstance(string.Join("\n", e.Args));
                Shutdown();
                return;
            }
            _singleInstance.StartListening(OnPipeCommand);

            Config = new ConfigManager(Paths.ConfigFile);
            await Config.LoadAsync();
            Logger.Info($"Config loaded: {Config.Config.Services.Count} service(s).");

            // Detect whether the .exe was moved since last run. The reapply
            // step below already rewrites shortcuts/autostart/protocol with
            // the current path on every launch — this just makes the move
            // explicit in the log and arms the one-shot Hub banner.
            DetectAndPersistExeLocation();

            Favicon = new FaviconService(Paths.IconsDir, new MacOSIconsCache(Paths.DataDir));
            Usage = new UsageTracker(Paths.UsageLogFile);
            Logger.Info($"Usage log: {Paths.UsageLogFile}");

            // Start perf sampler before anything heavy so the t=0 row
            // captures cold-start memory (useful baseline for later
            // comparisons with hibernation / startup-tuning changes).
            Perf = new PerfMonitor(Paths.DataDir);

            var envOptions = new CoreWebView2EnvironmentOptions();
            Logger.Info("Creating shared CoreWebView2Environment...");
            WebViewEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Paths.WebView2DataDir,
                options: envOptions);
            Logger.Info("CoreWebView2Environment ready.");

            // Theme follows Windows system setting. We apply the current
            // theme up-front so the very first Hub paint matches, then let
            // SystemThemeWatcher (set up on each FluentWindow) handle live
            // changes via WM_SETTINGCHANGE. Additionally, listen via the
            // .NET SystemEvents bridge — it's a more reliable source than
            // the WPF-UI watcher's HwndSource hook (which in 4.3.0 can
            // miss the broadcast if no FluentWindow has captured the OS
            // session yet) and guarantees a re-apply even when only a
            // hidden/tray window is alive.
            ApplicationThemeManager.Apply(
                ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                updateAccent: true);

            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Exit += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            Hotkeys = new HotkeyManager();
            DoubleCtrlC = new DoubleCtrlCDetector(Dispatcher);
            Windows = new WindowManager();

            // Replay any pipe-args that arrived during OnStartup before
            // Windows was ready (parallel-autostart from HKCU\Run).
            DrainPendingPipeArgs();

            ReapplyAllHotkeys();
            ReapplyAllDoubleCtrlC();
            ReapplyAllProtocolHandlers();
            ReapplyShortcutsAndAutostart();
            ReapplyRaycastScripts();

            HandleLaunchArgs(e.Args, isFirstLaunch: true, useHiddenStart: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal startup error", ex);
            MessageBox.Show(
                $"Failed to start WebViewHub:\n\n{ex.Message}\n\n{ex.StackTrace}\n\n" +
                $"Logs: {Logger.LogsDir}",
                "WebViewHub — startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Fires when Windows user preferences change. We only care about the
    /// General category, which is what gets broadcast for the light/dark
    /// app-mode toggle. Re-applies the current system theme so every
    /// FluentWindow (open or yet-to-open) gets the correct backdrop and
    /// resource colours without an app restart.
    /// </summary>
    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        try
        {
            // Marshal to UI thread — SystemEvents callbacks fire on a
            // background thread, but ApplicationThemeManager.Apply touches
            // WPF resource dictionaries.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var sysTheme = ApplicationThemeManager.GetSystemTheme();
                var appTheme = sysTheme == SystemTheme.Dark
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;
                if (ApplicationThemeManager.GetAppTheme() == appTheme) return;
                Logger.Info($"System theme changed to {sysTheme} — re-applying.");
                ApplicationThemeManager.Apply(
                    appTheme,
                    Wpf.Ui.Controls.WindowBackdropType.Mica,
                    updateAccent: true);
            }));
        }
        catch (Exception ex) { Logger.Warn($"OnUserPreferenceChanged failed: {ex.Message}"); }
    }

    /// <summary>
    /// Hooks for unhandled exceptions across all the threads WPF uses:
    /// UI dispatcher, background ThreadPool, and unobserved Tasks.
    /// We log + try to keep the app alive when it's safe.
    /// </summary>
    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            Logger.Error("DispatcherUnhandledException — UI thread", e.Exception);
            // Keep the app alive: most UI exceptions are recoverable
            // (a single broken event handler shouldn't kill everything).
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Logger.Error(
                $"AppDomain.UnhandledException (terminating={e.IsTerminating})",
                e.ExceptionObject as Exception);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Logger.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Registers (or re-registers) the global hotkey for one service.
    /// Returns false if the combination is invalid or already taken.
    /// </summary>
    public static bool ReapplyHotkey(Models.ServiceConfig svc)
    {
        Hotkeys.Remove(svc.Id);
        if (string.IsNullOrWhiteSpace(svc.Hotkey)) return true;
        return Hotkeys.Apply(svc.Id, svc.Hotkey, () => Windows.OpenOrToggleService(svc));
    }

    public static void ReapplyAllHotkeys()
    {
        foreach (var svc in Config.Config.Services)
        {
            ReapplyHotkey(svc);
        }
    }

    /// <summary>
    /// (Re)wires the double-Ctrl+C subscription for one service. Call
    /// after any edit. Service must be a translator AND have UseDoubleCtrlC
    /// for the subscription to be active.
    /// </summary>
    public static void ReapplyDoubleCtrlC(Models.ServiceConfig svc)
    {
        if (_doubleCtrlCSubs.TryGetValue(svc.Id, out var existing))
        {
            existing.Dispose();
            _doubleCtrlCSubs.Remove(svc.Id);
        }

        if (svc.IsTranslator && svc.UseDoubleCtrlC)
        {
            var sub = DoubleCtrlC.Subscribe(() => Windows.OpenTranslator(svc));
            _doubleCtrlCSubs[svc.Id] = sub;
        }
    }

    public static void ReapplyAllDoubleCtrlC()
    {
        foreach (var svc in Config.Config.Services)
        {
            ReapplyDoubleCtrlC(svc);
        }
    }

    public static void RemoveDoubleCtrlC(string serviceId)
    {
        if (_doubleCtrlCSubs.TryGetValue(serviceId, out var sub))
        {
            sub.Dispose();
            _doubleCtrlCSubs.Remove(serviceId);
        }
    }

    /// <summary>
    /// Writes (or removes) the HKCU registry handler for one service's
    /// custom URI scheme. Always runs the unregister branch first so a
    /// rename or scheme change leaves no dangling key.
    /// </summary>
    public static void ReapplyProtocolHandler(Models.ServiceConfig svc)
    {
        // Best-effort; never crash window creation over registry hiccups.
        try
        {
            if (!string.IsNullOrWhiteSpace(svc.ProtocolScheme))
            {
                ProtocolHandlerManager.Unregister(svc.ProtocolScheme, svc.Id);
            }

            if (svc.RegisterProtocol && !string.IsNullOrWhiteSpace(svc.ProtocolScheme))
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                              ?? Path.Combine(AppContext.BaseDirectory, "WebViewHub.exe");
                ProtocolHandlerManager.Register(svc.ProtocolScheme, svc.Id, exePath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"ReapplyProtocolHandler '{svc.Name}': {ex.Message}");
        }
    }

    public static void ReapplyAllProtocolHandlers()
    {
        foreach (var svc in Config.Config.Services)
        {
            ReapplyProtocolHandler(svc);
        }
    }

    public static void RemoveProtocolHandler(Models.ServiceConfig svc)
    {
        if (string.IsNullOrWhiteSpace(svc.ProtocolScheme)) return;
        ProtocolHandlerManager.Unregister(svc.ProtocolScheme, svc.Id);
    }

    /// <summary>
    /// Walks the config and ensures Start-menu .lnks and HKCU Run entries
    /// match the AutoStart/HasShortcut flags (and rewrites the exe path
    /// inside them to whatever this running instance's path is).
    /// </summary>
    public static void ReapplyShortcutsAndAutostart()
    {
        // Hub shortcut / autostart respect the HubSettings flags.
        ApplyHubLaunchSettings();

        var shortcuts = 0;
        var autostarts = 0;
        foreach (var svc in Config.Config.Services)
        {
            if (ReapplyShortcut(svc) && svc.HasShortcut) shortcuts++;
            if (ReapplyAutostart(svc) && svc.AutoStart) autostarts++;
        }

        if (WasRelocated)
        {
            Logger.Info($"Relocation refresh: rewrote hub shortcut + {shortcuts} service shortcut(s) and {autostarts} autostart entr{(autostarts == 1 ? "y" : "ies")} to new exe path.");
        }
    }

    /// <summary>
    /// Applies <see cref="HubSettings.HasShortcut"/> and
    /// <see cref="HubSettings.AutoStart"/> to OS state — creates/removes the
    /// Hub Start-menu .lnk and writes/clears the HKCU\Run entry. Idempotent;
    /// called on startup AND from the Hub-settings dialog after Save.
    /// </summary>
    public static void ApplyHubLaunchSettings()
    {
        var s = Config.Config.HubSettings;

        // Hub Start-menu shortcut.
        try
        {
            if (s.HasShortcut)
                ShortcutManager.CreateHub();
            else
                ShortcutManager.RemoveHub();
        }
        catch (Exception ex) { Logger.Warn($"Hub shortcut apply failed: {ex.Message}"); }

        // Hub HKCU\Run autostart. Separate value name from any per-service
        // entry so toggling Hub autostart doesn't collide with services.
        try
        {
            AutostartManager.ApplyHub(s.AutoStart, s.StartHidden);
        }
        catch (Exception ex) { Logger.Warn($"Hub autostart apply failed: {ex.Message}"); }

        Logger.Info(
            $"ApplyHubLaunchSettings → hasShortcut={s.HasShortcut} " +
            $"autoStart={s.AutoStart} startHidden={s.StartHidden}");
    }

    /// <summary>
    /// Compares the current .exe path to the one stored from the previous
    /// run. If they differ, we treat this startup as a "move" and log it
    /// loudly so the user can confirm in the log that quick-launch links
    /// have been healed. Saves the current path back to config either way
    /// (first run + every move) so subsequent launches can detect future
    /// relocations.
    ///
    /// Note: the actual rewriting of shortcuts, autostart entries, and
    /// Raycast scripts happens unconditionally elsewhere on every startup —
    /// the registrations always point at the running instance's exe path.
    /// This method only adds detection + observability.
    /// </summary>
    private static void DetectAndPersistExeLocation()
    {
        var currentExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                             ?? Path.Combine(AppContext.BaseDirectory, "WebViewHub.exe");

        var lastPath = Config.Config.LastExePath;
        var firstRun = string.IsNullOrEmpty(lastPath);
        var moved = !firstRun && !string.Equals(lastPath, currentExePath, StringComparison.OrdinalIgnoreCase);

        if (moved)
        {
            WasRelocated = true;
            PreviousExePath = lastPath;
            Logger.Info("==== App was relocated since last run ====");
            Logger.Info($"  previous : {lastPath}");
            Logger.Info($"  current  : {currentExePath}");
            Logger.Info("Refreshing Start-menu shortcuts, HKCU\\Run autostart, and Raycast scripts so quick-launch links work from the new path.");
        }
        else if (firstRun)
        {
            Logger.Info($"First run from {currentExePath}. Tracking exe path for relocation detection.");
        }

        if (firstRun || moved)
        {
            Config.Config.LastExePath = currentExePath;
            try { Config.SaveSync(); }
            catch (Exception ex)
            {
                // Save failure is non-fatal — we just won't detect the
                // next move. Everything else still works.
                Logger.Error("Failed to persist LastExePath after relocation detection", ex);
            }
        }
    }

    public static bool ReapplyShortcut(Models.ServiceConfig svc)
    {
        try
        {
            if (!svc.HasShortcut)
            {
                ShortcutManager.Remove(svc.Name);
                return true;
            }

            var iconPath = ResolveIconForShortcut(svc);
            // Same AUMID format as the runtime window — that's what makes
            // pinning + window-grouping behave correctly.
            var aumid = $"WebViewHub.Service.{svc.Id}";
            ShortcutManager.Create(svc.Name, svc.Id, iconPath, aumid);
            return true;
        }
        catch (Exception ex)
        {
            // Shortcut creation isn't critical — never fail the app over it.
            Logger.Warn($"ReapplyShortcut failed for '{svc.Name}': {ex.Message}");
            return false;
        }
    }

    public static bool ReapplyAutostart(Models.ServiceConfig svc)
    {
        try
        {
            AutostartManager.Apply(svc.Id, svc.Id, svc.AutoStart);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"ReapplyAutostart failed for '{svc.Name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Regenerates one .ps1 per service in the Raycast scripts directory
    /// and prunes orphans. Call after any change to the service list.
    /// </summary>
    public static void ReapplyRaycastScripts()
    {
        try
        {
            RaycastScriptManager.Sync(Config.Config.Services, Paths.IconsDir);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ReapplyRaycastScripts failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the HKCU registry keys we used to write for the
    /// 'webviewhub://' URL protocol + App Paths/Applications resolution.
    /// Idempotent — silent no-op if the keys are already gone.
    /// </summary>
    private static void CleanupLegacyProtocolRegistration()
    {
        var keys = new[]
        {
            @"Software\Classes\webviewhub",
            @"Software\Microsoft\Windows\CurrentVersion\App Paths\WebViewHub.exe",
            @"Software\Classes\Applications\WebViewHub.exe",
        };
        foreach (var path in keys)
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch (Exception ex) { Logger.Warn($"Legacy key cleanup failed for HKCU\\{path}: {ex.Message}"); }
        }
    }

    private static string? ResolveIconForShortcut(Models.ServiceConfig svc)
    {
        // Prefer the user-supplied custom icon, fall back to cached favicon.
        var sourcePath = !string.IsNullOrEmpty(svc.CustomIconPath) && File.Exists(svc.CustomIconPath)
            ? svc.CustomIconPath
            : Path.Combine(Paths.IconsDir, $"{svc.Id}.png");

        if (!File.Exists(sourcePath)) return null;
        return Helpers.IconHelper.EnsureIcoFile(sourcePath);
    }

    private void OnPipeCommand(string command)
    {
        Logger.Info($"OnPipeCommand received: '{command?.Replace("\n", " | ")}'");
        Dispatcher.Invoke(() =>
        {
            var args = (command ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (Windows == null)
            {
                // OnStartup hasn't created WindowManager yet — queue and
                // replay from DrainPendingPipeArgs once it's ready.
                lock (_pendingPipeArgsLock) _pendingPipeArgs.Add(args);
                Logger.Info($"OnPipeCommand queued (Windows not ready): [{string.Join(", ", args)}]");
                return;
            }
            Logger.Info($"OnPipeCommand dispatched: [{string.Join(", ", args)}]");
            HandleLaunchArgs(args, isFirstLaunch: false);
        });
    }

    private void DrainPendingPipeArgs()
    {
        string[][] toReplay;
        lock (_pendingPipeArgsLock)
        {
            if (_pendingPipeArgs.Count == 0) return;
            toReplay = _pendingPipeArgs.ToArray();
            _pendingPipeArgs.Clear();
        }
        foreach (var args in toReplay)
        {
            Logger.Info($"Replaying queued pipe args: [{string.Join(", ", args)}]");
            // Queued during OnStartup = parallel autostart entries forwarded
            // through the named pipe. They want the same hidden-start
            // treatment as the master's own first-launch args.
            try { HandleLaunchArgs(args, isFirstLaunch: false, useHiddenStart: true); }
            catch (Exception ex) { Logger.Error("Replaying queued pipe args failed", ex); }
        }
    }

    /// <summary>
    /// Resolves the launch intent from CLI args. Handles three forms:
    ///  1. "--service=&lt;id-or-name&gt; --uri=&lt;custom-uri&gt;" — registered
    ///     protocol invocation. Routes through the URL translator.
    ///  2. "--service=&lt;id-or-name&gt;" — Raycast / Start-menu shortcut.
    ///  3. A bare "scheme:..." token — same as (1) but without --service.
    ///     We look up the service by registered protocol.
    ///  4. No args — show the hub.
    /// </summary>
    /// <param name="useHiddenStart">
    /// Pass true for Windows-autostart paths (first-launch CLI args + pipe
    /// args drained during OnStartup). When the resolved service has
    /// <see cref="Models.ServiceConfig.StartMinimized"/>=true, the window
    /// inits invisibly and goes straight to the tray. Pass false for
    /// user-initiated paths (hotkey, hub click, post-startup pipe re-invoke,
    /// Raycast launches) so the window shows immediately on first trigger.
    /// </param>
    private void HandleLaunchArgs(string[] args, bool isFirstLaunch, bool useHiddenStart = false)
    {
        Logger.Info($"HandleLaunchArgs: isFirstLaunch={isFirstLaunch} useHiddenStart={useHiddenStart}, args=[{string.Join(", ", args)}]");

        var service = ParseStringArg(args, "--service=");
        var uri = ParseStringArg(args, "--uri=");

        // Bare custom-scheme token (e.g. "spotify:track:abc"). Some shells
        // hand the URI directly as argv[0+1] instead of inside --uri.
        if (string.IsNullOrEmpty(uri))
        {
            foreach (var a in args)
            {
                if (a.StartsWith("--", StringComparison.Ordinal)) continue;
                if (ProtocolUrlTranslator.LooksLikeProtocolArg(a, out _))
                {
                    uri = a;
                    break;
                }
            }
        }

        if (!string.IsNullOrEmpty(uri) && ProtocolUrlTranslator.LooksLikeProtocolArg(uri, out var scheme))
        {
            Models.ServiceConfig? svc = null;
            if (!string.IsNullOrEmpty(service))
            {
                svc = Config.FindById(service) ?? Config.FindByName(service);
            }
            svc ??= Config.Config.Services.FirstOrDefault(s =>
                s.RegisterProtocol &&
                string.Equals(s.ProtocolScheme, scheme, StringComparison.OrdinalIgnoreCase));

            if (svc != null)
            {
                var translated = ProtocolUrlTranslator.Translate(scheme, uri, svc.Url);
                Logger.Info($"Protocol launch '{uri}' → service '{svc.Name}' → {translated}");
                Windows.OpenServiceWithUrl(svc, translated);
                return;
            }

            Logger.Warn($"Protocol launch '{uri}' has no matching service; falling back to hub.");
        }

        if (!string.IsNullOrEmpty(service))
        {
            Logger.Info($"Open service '{service}'.");
            Windows.OpenOrToggleService(service, useHiddenStart);
            return;
        }

        // --start-hidden: launched by the Hub autostart entry asking for
        // a silent boot. EnsureHub creates the HubWindow + tray icon but
        // keeps the window hidden — user can surface it via tray click.
        // Only meaningful on first launch (later pipe invocations always
        // mean the user wants the Hub visible).
        if (isFirstLaunch && args.Any(a => string.Equals(a, "--start-hidden", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.Info("Startup mode: hub (hidden in tray, --start-hidden).");
            Windows.EnsureHubInTray();
            return;
        }

        if (isFirstLaunch) Logger.Info("Startup mode: hub.");
        Windows.ShowHub();
    }

    private static string? ParseStringArg(string[] args, string prefix)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return a.Substring(prefix.Length).Trim('"', ' ');
            }
        }
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var sub in _doubleCtrlCSubs.Values) sub.Dispose();
        _doubleCtrlCSubs.Clear();
        DoubleCtrlC?.Dispose();
        Hotkeys?.Dispose();
        Perf?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
