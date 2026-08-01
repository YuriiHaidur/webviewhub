using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace WebViewHub.Services;

/// <summary>
/// Registers process-wide global hotkeys via WinAPI RegisterHotKey.
/// All hotkeys are owned by a single message-only HwndSource so they
/// keep working even when no service window is open.
/// </summary>
public class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;
    private const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private readonly HwndSource _source;
    private int _nextId = 1;

    /// <summary>id → (serviceId, callback, hotkeyText).</summary>
    private readonly Dictionary<int, (string ServiceId, Action Callback)> _byId = new();
    private readonly Dictionary<string, int> _byServiceId = new();

    public HotkeyManager()
    {
        // HWND_MESSAGE = -3. A message-only window has no UI but receives
        // WM_HOTKEY broadcasts just like a regular one.
        var parameters = new HwndSourceParameters("WebViewHub.HotkeyHost")
        {
            ParentWindow = new IntPtr(-3)
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public int RegisteredCount => _byId.Count;

    /// <summary>
    /// Adds or replaces the hotkey for the given service id.
    /// Returns true if registered, false if hotkeyText is invalid or the
    /// combination is already taken by another app.
    /// </summary>
    public bool Apply(string serviceId, string? hotkeyText, Action callback)
    {
        Remove(serviceId);

        if (string.IsNullOrWhiteSpace(hotkeyText)) return false;
        if (!TryParse(hotkeyText, out var mods, out var vk))
        {
            Logger.Warn($"[Hotkey] Apply '{hotkeyText}' for {serviceId} — TryParse failed.");
            return false;
        }

        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk))
        {
            var err = Marshal.GetLastWin32Error();
            Logger.Warn($"[Hotkey] RegisterHotKey id={id} '{hotkeyText}' (mods=0x{mods:X} vk=0x{vk:X}) for {serviceId} FAILED, error={err}.");
            return false;
        }

        _byId[id] = (serviceId, callback);
        _byServiceId[serviceId] = id;
        Logger.Info($"[Hotkey] Registered id={id} '{hotkeyText}' (mods=0x{mods:X} vk=0x{vk:X}) for {serviceId}.");
        return true;
    }

    public void Remove(string serviceId)
    {
        if (!_byServiceId.TryGetValue(serviceId, out var id)) return;
        UnregisterHotKey(_source.Handle, id);
        _byServiceId.Remove(serviceId);
        _byId.Remove(id);
        Logger.Debug($"[Hotkey] Unregistered id={id} for {serviceId}.");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        var id = wParam.ToInt32();
        if (_byId.TryGetValue(id, out var entry))
        {
            handled = true;
            Logger.Debug($"[Hotkey] WM_HOTKEY id={id} → {entry.ServiceId} (foreground HWND=0x{NativeGetForegroundWindow():X}).");
            try { entry.Callback(); }
            catch (Exception ex) { Logger.Error($"[Hotkey] Callback for {entry.ServiceId} threw", ex); }
        }
        else
        {
            Logger.Debug($"[Hotkey] WM_HOTKEY id={id} — no matching entry (already removed?).");
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static long NativeGetForegroundWindow()
    {
        try { return GetForegroundWindow().ToInt64(); }
        catch { return 0; }
    }

    /// <summary>
    /// Parses strings like "Ctrl+Alt+S" or "Win+Shift+F12" into Win32
    /// modifier flags + virtual-key code.
    /// </summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false; // global hotkey requires a modifier + key

        Key? key = null;
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    if (!Enum.TryParse<Key>(p, ignoreCase: true, out var k))
                        return false;
                    if (key.HasValue) return false; // more than one non-modifier key
                    key = k;
                    break;
            }
        }

        if (modifiers == 0 || key is null) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return true;
    }

    /// <summary>
    /// Best-effort normalization for display: "ctrl + alt + s" → "Ctrl+Alt+S".
    /// </summary>
    public static string Normalize(string text)
    {
        if (!TryParse(text, out var mods, out var vk)) return text;
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        var k = KeyInterop.KeyFromVirtualKey((int)vk);
        parts.Add(k.ToString());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        foreach (var id in _byId.Keys.ToList())
        {
            UnregisterHotKey(_source.Handle, id);
        }
        _byId.Clear();
        _byServiceId.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
