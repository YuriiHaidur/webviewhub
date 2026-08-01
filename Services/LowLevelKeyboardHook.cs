using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WebViewHub.Services;

/// <summary>
/// Process-wide WH_KEYBOARD_LL hook. Observes key presses globally
/// without consuming them — the OS still delivers the keystroke to
/// whatever window has focus, so e.g. Ctrl+C still copies normally.
///
/// The hook is installed from — and the callback runs on — a dedicated
/// background thread that owns a private <see cref="Dispatcher"/> /
/// message pump. This isolation is non-optional: WH_KEYBOARD_LL marshals
/// every event to the thread that called <c>SetWindowsHookEx</c>, and
/// Windows enforces a <c>LowLevelHooksTimeout</c> (~300ms) — if the
/// thread is busy, the event is silently dropped. Running on the UI
/// thread caused us to miss Ctrl-down events whenever WPF was busy
/// creating WebView2 windows, breaking Ctrl+C+C detection.
///
/// Subscribers MUST marshal back to the UI thread themselves before
/// touching WPF state or the clipboard.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// Fires for every key-down. Runs on the dedicated hook thread —
    /// listeners must NOT block (would re-introduce the very timeout
    /// we moved off the UI thread to escape) and must marshal back to
    /// the UI dispatcher before touching WPF state.
    /// </summary>
    public event Action<uint>? KeyDown;
    public event Action<uint>? KeyUp;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;
    private Thread? _hookThread;
    private Dispatcher? _hookDispatcher;
    private readonly ManualResetEventSlim _installReady = new(false);

    /// <summary>Total keystrokes seen by our callback since install.
    /// Compared against wall-clock to detect "hook went silent" (= Windows
    /// silently uninstalled it on LowLevelHooksTimeout).</summary>
    private long _eventsSeen;
    private DateTime _lastEventAt = DateTime.MinValue;
    private DateTime _installedAt = DateTime.MinValue;
    private readonly object _diagLock = new();

    public void Install()
    {
        if (_hookThread != null) return;

        // Dedicated thread for the hook so a busy WPF UI thread can never
        // starve it. Background so it doesn't block process shutdown.
        _hookThread = new Thread(HookThreadMain)
        {
            Name = "WebViewHub.KbdHook",
            IsBackground = true
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();

        // Wait briefly so callers can rely on "after Install(), the hook
        // is either active or has failed". Without this, the first key
        // events that race the install could be lost.
        if (!_installReady.Wait(TimeSpan.FromSeconds(3)))
        {
            Logger.Warn("LowLevelKeyboardHook: Install() readiness wait timed out.");
        }
    }

    private void HookThreadMain()
    {
        try
        {
            _proc = HookCallback;
            var hModule = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hModule, 0);
            if (_hookId == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Logger.Warn($"SetWindowsHookEx failed, error={err}");
                _installReady.Set();
                return;
            }

            _installedAt = DateTime.UtcNow;
            _hookDispatcher = Dispatcher.CurrentDispatcher;
            Logger.Info($"LowLevelKeyboardHook installed on dedicated thread " +
                        $"(TID={Environment.CurrentManagedThreadId}) at {_installedAt:HH:mm:ss.fff}.");
            _installReady.Set();

            // Drive the message pump so Windows can deliver hook callbacks
            // to this thread. Dispatcher.Run blocks until InvokeShutdown.
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            Logger.Error("LowLevelKeyboardHook thread crashed", ex);
            _installReady.Set();
        }
        finally
        {
            // Defensive — Dispose() normally unhooks first, but if the
            // pump exits for any other reason we must still release the
            // hook so the OS isn't left holding our delegate.
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Snapshot of hook activity since install. Read this from a timer or
    /// at known checkpoints to detect "hook went silent" — if EventsSeen
    /// hasn't grown across a period of known user input, Windows has
    /// almost certainly unhooked us on LowLevelHooksTimeout.
    /// </summary>
    public (long EventsSeen, TimeSpan SinceLastEvent, TimeSpan SinceInstall) GetDiagSnapshot()
    {
        lock (_diagLock)
        {
            var now = DateTime.UtcNow;
            var sinceLast = _lastEventAt == DateTime.MinValue
                ? TimeSpan.Zero
                : now - _lastEventAt;
            var sinceInstall = _installedAt == DateTime.MinValue
                ? TimeSpan.Zero
                : now - _installedAt;
            return (_eventsSeen, sinceLast, sinceInstall);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var msg = wParam.ToInt32();
                long ev;
                TimeSpan gap;
                lock (_diagLock)
                {
                    var now = DateTime.UtcNow;
                    gap = _lastEventAt == DateTime.MinValue ? TimeSpan.Zero : now - _lastEventAt;
                    _lastEventAt = now;
                    _eventsSeen++;
                    ev = _eventsSeen;
                }
                if (ev <= 5 || gap.TotalMilliseconds > 2000)
                {
                    Logger.Debug($"[KbdHook] ev#{ev} vk=0x{kbd.vkCode:X} msg=0x{msg:X} gap={gap.TotalMilliseconds:F0}ms");
                }
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    KeyDown?.Invoke(kbd.vkCode);
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    KeyUp?.Invoke(kbd.vkCode);
                }
            }
            catch (Exception ex)
            {
                // A throw here would crash the hook chain — be paranoid.
                Logger.Error("LowLevelKeyboardHook callback", ex);
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        // Stops Dispatcher.Run() inside HookThreadMain, which then exits
        // cleanly through the finally block.
        _hookDispatcher?.InvokeShutdown();
        _hookThread = null;
        _proc = null;
        _installReady.Dispose();
    }
}
