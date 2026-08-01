using System.Runtime.InteropServices;
using WebViewHub.Services;

namespace WebViewHub.Helpers;

/// <summary>
/// P/Invoke for setting per-window AppUserModel properties via the
/// shell IPropertyStore.
///
/// Why this exists, in plain English:
///
/// • PKEY_AppUserModel_ID alone is enough to split running windows into
///   distinct taskbar groups (that's why our tray + open windows look
///   right).
///
/// • But when the user right-clicks a running window → "Pin to taskbar",
///   Windows creates a static .lnk in User Pinned\TaskBar and snapshots
///   FOUR window properties into it: ID, RelaunchCommand,
///   RelaunchIconResource, RelaunchDisplayNameResource.
///   If we don't set the last three, Windows falls back to the exe's
///   own metadata — which is why every pinned service ends up showing
///   "WebViewHub" with the generic icon.
///
/// • Setting all four correctly makes Windows pin a shortcut that
///   re-launches us with --service=&lt;id&gt; and shows the right name and
///   icon.
/// </summary>
internal static class NativeMethods
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid riid,
        out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    private const ushort VT_LPWSTR = 31;

    private static readonly Guid AppUserModelGuid =
        new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static PropertyKey PKEY_AppUserModel_ID = new()
    { fmtid = AppUserModelGuid, pid = 5 };
    private static PropertyKey PKEY_AppUserModel_RelaunchCommand = new()
    { fmtid = AppUserModelGuid, pid = 2 };
    private static PropertyKey PKEY_AppUserModel_RelaunchIconResource = new()
    { fmtid = AppUserModelGuid, pid = 3 };
    private static PropertyKey PKEY_AppUserModel_RelaunchDisplayNameResource = new()
    { fmtid = AppUserModelGuid, pid = 4 };

    /// <summary>
    /// Sets every property the taskbar pinning code path needs in one
    /// shot. Call from OnSourceInitialized; safe to call again later
    /// (e.g. when the favicon finishes downloading).
    ///
    /// Pass null for any property you don't want to overwrite.
    /// </summary>
    public static void SetWindowAppProperties(
        IntPtr hwnd,
        string? appId,
        string? relaunchCommand,
        string? displayName,
        string? iconResource)
    {
        if (hwnd == IntPtr.Zero) return;

        // Whole method is best-effort: taskbar pinning identity is nice
        // to have, but a failure here must NEVER abort window creation.
        // Some Windows builds / shell states return S_OK but a null store,
        // which is what was crashing OnSourceInitialized in the wild.
        IPropertyStore? store = null;
        try
        {
            Logger.Debug($"SetWindowAppProperties: SHGetPropertyStoreForWindow(hwnd=0x{hwnd:X})");
            var iidPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            var hr = SHGetPropertyStoreForWindow(hwnd, ref iidPropertyStore, out store);
            Logger.Debug($"  HR=0x{hr:X8} store={(store is null ? "null" : "ok")}");
            if (hr != 0 || store is null) return;

            // Only AUMID is currently set. RelaunchCommand /
            // RelaunchDisplayNameResource / RelaunchIconResource were
            // observed to natively crash on Windows 11 26100 when applied
            // through SetValue here (no managed exception, just process
            // termination). Tracked for re-enable via a different path
            // (setting the same properties on the .lnk file directly).
            Logger.Debug($"  SetValue ID='{appId}'");
            TrySetStringProperty(store, ref PKEY_AppUserModel_ID, appId);

            if (!string.IsNullOrEmpty(relaunchCommand))
                Logger.Debug("  RelaunchCommand suppressed (Win11 native-crash workaround)");
            if (!string.IsNullOrEmpty(displayName))
                Logger.Debug("  RelaunchDisplayNameResource suppressed");
            if (!string.IsNullOrEmpty(iconResource))
                Logger.Debug("  RelaunchIconResource suppressed");

            Logger.Debug("  Commit()");
            try { store.Commit(); }
            catch (Exception ex) { Logger.Error("  Commit failed", ex); }
            Logger.Debug("  done");
        }
        catch (Exception ex)
        {
            Logger.Error("SetWindowAppProperties failed", ex);
        }
        finally
        {
            if (store is not null && Marshal.IsComObject(store))
            {
                try { Marshal.ReleaseComObject(store); } catch { }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Foreground / show — robust tray-restore primitives
    // ────────────────────────────────────────────────────────────────────
    //
    // WPF Window.Show() + Activate() can fail to actually bring a previously
    // Hide()-d window to the foreground (especially after a hidden start)
    // because Win32 considers it "still active" and the Z-order request gets
    // dropped. SetForegroundWindow alone is ALSO insufficient: Windows
    // refuses the call when the calling process isn't already the
    // foreground process (the "no focus steal" policy). The user's
    // WH_KEYBOARD_LL Ctrl+C+C trigger doesn't grant us foreground
    // privilege (low-level hooks are observers — unlike WM_HOTKEY they
    // don't transfer foreground rights), so a plain SetForegroundWindow
    // silently falls back to a taskbar-flash and the translator window
    // ends up created but stuck behind whatever app the user was in.
    //
    // The classic workaround is the AttachThreadInput trick: attach our
    // UI thread to the current foreground window's input queue for the
    // duration of the SetForegroundWindow call. While attached, Windows
    // treats our SetForegroundWindow as if it came from the foreground
    // process itself — the policy check passes. We then immediately
    // detach so we don't share input state with the other app.

    private const int SW_SHOWNORMAL = 1;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>
    /// Forces the window to a visible, restored state and pulls it to the
    /// foreground — even when the calling process doesn't currently have
    /// foreground privilege. Idempotent — safe to call even when the
    /// window is already visible.
    ///
    /// Implementation notes:
    /// <list type="bullet">
    /// <item>If the foreground window is owned by another process, attach
    ///   our thread to its input queue for the duration of the
    ///   SetForegroundWindow / BringWindowToTop calls. Windows then
    ///   honours the foreground request as if it came from the user.</item>
    /// <item>If <see cref="GetForegroundWindow"/> returns our own HWND or
    ///   a same-thread HWND, skip the attach — it's both unnecessary and
    ///   would deadlock on AttachThreadInput.</item>
    /// <item>Always detach afterwards (even on exception) — leaving the
    ///   attachment in place would couple our input state to a stranger
    ///   process for the rest of the session, causing focus / mouse
    ///   capture bugs that are nightmarish to track down.</item>
    /// </list>
    /// </summary>
    public static void ForceShowAndForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            ShowWindow(hwnd, SW_SHOW);

            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == hwnd)
            {
                // No foreground or we're already it — plain call works.
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
                return;
            }

            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var currentThread = GetCurrentThreadId();
            if (foregroundThread == 0 || foregroundThread == currentThread)
            {
                // Same input queue — attach would be a no-op or deadlock.
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
                return;
            }

            var attached = AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"ForceShowAndForeground failed: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  DWM cloaking — invisible window without offscreen tricks
    // ────────────────────────────────────────────────────────────────────
    //
    // Setting DWMWA_CLOAK on a window tells the Desktop Window Manager to
    // continue composing it (so paint, layout, and child controls all work
    // normally) while never actually displaying it on screen. This is the
    // technique used by Start menu, Explorer, and WinUI3's "hidden start"
    // pattern — strictly better than offscreen positioning, since it has
    // no race with DWM's initial paint and no chance of bleed onto a
    // negative-coordinate monitor.

    private const int DWMWA_CLOAK = 13;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// Cloaks (true) or uncloaks (false) the window via DWM. While cloaked,
    /// the window participates in composition normally but is invisible to
    /// the user — no flash, no leak onto any monitor.
    /// </summary>
    public static bool SetCloak(IntPtr hwnd, bool cloak)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var value = cloak ? 1 : 0;
            var hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
            if (hr != 0)
            {
                Logger.Warn($"SetCloak({cloak}) returned HR=0x{hr:X8}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SetCloak({cloak}) failed: {ex.Message}");
            return false;
        }
    }

    private static void TrySetStringProperty(IPropertyStore store, ref PropertyKey key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        // ENTIRE body in try-catch — observed in the wild that an NRE
        // raised somewhere inside the marshalling/SetValue path was
        // escaping the previous narrower try (likely JIT inlining of
        // this method merging the call site stack with the caller).
        PropVariant pv = default;
        try
        {
            pv = new PropVariant
            {
                vt = VT_LPWSTR,
                pwszVal = Marshal.StringToCoTaskMemUni(value)
            };
            if (store is null) return;
            store.SetValue(ref key, ref pv);
        }
        catch (Exception ex)
        {
            Logger.Error($"TrySetStringProperty(pid={key.pid}, value='{value}')", ex);
        }
        finally
        {
            try { PropVariantClear(ref pv); } catch { }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Multi-DPI window icons — LoadImage + WM_SETICON
    // ────────────────────────────────────────────────────────────────────
    //
    // WPF Window.Icon takes ImageSource and packs it into a single-frame
    // HICON regardless of how many frames the source .ico has. The
    // taskbar then nearest-neighbour-downscales that one frame into its
    // slot, which is why our icons look soft / "small" next to native
    // apps. The fix is to bypass WPF's icon path and assign HICONs of the
    // exact DPI-aware slot sizes via WM_SETICON; LoadImage with explicit
    // cxDesired/cyDesired pulls the matching frame straight out of a
    // multi-frame .ico file without rescaling.
    //
    // Similarly NotifyIcon uses whatever-size HICON we hand it (known
    // dotnet/winforms#6955), so giving it an HICON sized exactly for the
    // tray slot (16 / 20 / 24 / 32 px depending on DPI) avoids tray-side
    // downscale and matches the visual mass of Discord/Telegram/Steam.

    public const uint IMAGE_ICON      = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTCOLOR = 0x00000000;
    public const uint LR_SHARED       = 0x00008000;

    public const uint WM_SETICON      = 0x0080;
    public const int  ICON_SMALL      = 0;   // titlebar + taskbar
    public const int  ICON_BIG        = 1;   // Alt+Tab / task switcher

    public const int  SM_CXSMICON     = 49;  // small icon (tray, titlebar)
    public const int  SM_CXICON       = 11;  // big icon (Alt+Tab)

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr hinst,
        string lpszName,
        uint uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Loads an HICON of exactly <paramref name="sizePx"/> from a .ico
    /// file on disk. Windows picks the matching frame from a multi-frame
    /// container (or scales the closest one) — no managed-side resize.
    /// Caller owns the handle and MUST call <see cref="DestroyIcon"/>.
    /// </summary>
    public static IntPtr LoadHiconFromFile(string icoPath, int sizePx)
    {
        if (string.IsNullOrEmpty(icoPath) || !System.IO.File.Exists(icoPath))
        {
            Logger.Warn($"[IconDbg] LoadHiconFromFile: missing path '{icoPath}'");
            return IntPtr.Zero;
        }
        var h = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, sizePx, sizePx,
            LR_LOADFROMFILE | LR_DEFAULTCOLOR);
        Logger.Debug($"[IconDbg] LoadHiconFromFile '{System.IO.Path.GetFileName(icoPath)}' size={sizePx}px → hIcon=0x{h:X}");
        return h;
    }

    /// <summary>
    /// DPI-aware (small, big) icon sizes for a given window's monitor.
    /// Small = titlebar/taskbar slot; big = Alt+Tab slot. Falls back to
    /// system-DPI metrics if per-window APIs are unavailable.
    /// </summary>
    public static (int small, int big) GetIconSizesForWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi >= 72)
                {
                    var small = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
                    var big   = GetSystemMetricsForDpi(SM_CXICON, dpi);
                    Logger.Debug($"[IconDbg] GetIconSizesForWindow hwnd=0x{hwnd:X} dpi={dpi} → small={small} big={big}");
                    return (small, big);
                }
            }
        }
        catch (Exception ex) { Logger.Warn($"[IconDbg] GetIconSizesForWindow per-window failed: {ex.Message}"); }
        var sSmall = GetSystemMetrics(SM_CXSMICON);
        var sBig   = GetSystemMetrics(SM_CXICON);
        Logger.Debug($"[IconDbg] GetIconSizesForWindow fallback (system DPI) → small={sSmall} big={sBig}");
        return (sSmall, sBig);
    }

    /// <summary>
    /// Returns the DPI-aware tray-slot pixel size for icons sent to
    /// NotifyIcon.
    ///
    /// Returns the BIG-icon metric (SM_CXICON, 32 at 100% DPI), not the
    /// SMALL-icon metric (SM_CXSMICON, 16 at 100% DPI), because:
    ///
    /// • Win11's redesigned taskbar tray actually displays icons at
    ///   ~24×24 (default taskbar height) — bigger than the legacy
    ///   16×16 SM_CXSMICON reports.
    /// • Microsoft never added a public API for the new tray slot size,
    ///   so the documented best-practice is to feed NotifyIcon an
    ///   over-sized HICON and let the shell downscale (sharp) instead
    ///   of feeding 16×16 and letting it upscale (blurry).
    /// • Discord, Telegram, Steam etc. all hand NotifyIcon a 32px HICON
    ///   for the same reason — that's why their icons look "bigger"
    ///   than ours did when we used SM_CXSMICON.
    ///
    /// Empirically: 32 at 96 DPI / 40 at 120 DPI / 48 at 144 DPI / 64 at
    /// 192 DPI — comfortably above any reported tray slot size, so the
    /// shell always downscales rather than upscales.
    /// </summary>
    public static int GetTrayIconSize(IntPtr hwnd)
    {
        return GetIconSizesForWindow(hwnd).big;
    }

    /// <summary>
    /// Sends WM_SETICON for both ICON_SMALL and ICON_BIG. Returns the
    /// previously-set handles (which the caller may DestroyIcon if it
    /// owns them; if WPF set them, leave them alone).
    /// </summary>
    public static void SendSetIcon(IntPtr hwnd, IntPtr hIconSmall, IntPtr hIconBig)
    {
        if (hwnd == IntPtr.Zero) return;
        if (hIconSmall != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconSmall);
        if (hIconBig   != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG,   hIconBig);
    }

    // ────────────────────────────────────────────────────────────────────
    //  AUMID on .lnk files (Stage 3 — proper taskbar pinning)
    // ────────────────────────────────────────────────────────────────────
    //
    // Writing AppUserModel.ID directly into a .lnk file is the canonical
    // way to make Windows associate the pinned taskbar shortcut with the
    // running window. Once both sides have the same AUMID, clicking the
    // pinned icon brings the existing window to front instead of spawning
    // a duplicate-looking entry.
    //
    // The .lnk is opened via IShellLinkW (COM CLSID_ShellLink), the file
    // is loaded with IPersistFile.Load(), the AUMID is set on the same
    // object via QI to IPropertyStore, and saved with IPersistFile.Save.

    private static readonly Guid CLSID_ShellLink =
        new("00021401-0000-0000-C000-000000000046");

    private const uint STGM_READWRITE = 0x00000002;

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>
    /// Embeds the given AppUserModel.ID into the .lnk file at lnkPath.
    /// Best-effort — failures are logged and swallowed so a hiccup here
    /// doesn't break shortcut creation overall.
    /// </summary>
    public static void SetShortcutAppUserModelId(string lnkPath, string aumid)
    {
        if (string.IsNullOrEmpty(lnkPath) || string.IsNullOrEmpty(aumid)) return;
        if (!System.IO.File.Exists(lnkPath))
        {
            Logger.Warn($"SetShortcutAumid: file not found '{lnkPath}'");
            return;
        }

        object? linkObj = null;
        try
        {
            Logger.Debug($"SetShortcutAumid '{System.IO.Path.GetFileName(lnkPath)}' → '{aumid}'");

            var shellLinkType = Type.GetTypeFromCLSID(CLSID_ShellLink, throwOnError: false);
            if (shellLinkType is null)
            {
                Logger.Warn("SetShortcutAumid: CLSID_ShellLink not registered");
                return;
            }

            linkObj = Activator.CreateInstance(shellLinkType);
            if (linkObj is null)
            {
                Logger.Warn("SetShortcutAumid: ShellLink instance is null");
                return;
            }

            var pf = (IPersistFile)linkObj;
            pf.Load(lnkPath, STGM_READWRITE);

            var ps = (IPropertyStore)linkObj;
            var pv = new PropVariant
            {
                vt = VT_LPWSTR,
                pwszVal = Marshal.StringToCoTaskMemUni(aumid)
            };
            try
            {
                ps.SetValue(ref PKEY_AppUserModel_ID, ref pv);
                ps.Commit();
            }
            finally
            {
                try { PropVariantClear(ref pv); } catch { }
            }

            // null filename + fRemember=true → save back to the file we loaded.
            pf.Save(null, true);
            Logger.Debug("SetShortcutAumid: saved.");
        }
        catch (Exception ex)
        {
            Logger.Error($"SetShortcutAumid failed for '{lnkPath}'", ex);
        }
        finally
        {
            if (linkObj is not null && Marshal.IsComObject(linkObj))
            {
                try { Marshal.ReleaseComObject(linkObj); } catch { }
            }
        }
    }
}
