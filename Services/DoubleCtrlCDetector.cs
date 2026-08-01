using System.Windows.Threading;

namespace WebViewHub.Services;

/// <summary>
/// Detects "Ctrl+C pressed twice within the threshold" using the
/// global low-level keyboard hook. The first Ctrl+C is allowed to
/// reach the focused app normally, so the OS populates the clipboard
/// with the selected text. After the second Ctrl+C we wait briefly
/// (clipboard write is asynchronous on some apps) and fire the
/// callback on the WPF UI thread.
///
/// Multiple subscribers (one per translator service) can register;
/// each receives the trigger.
/// </summary>
public sealed class DoubleCtrlCDetector : IDisposable
{
    private const uint VK_CONTROL = 0x11;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_C = 0x43;

    /// <summary>Max gap between the two Ctrl+C presses to count as a double-tap.</summary>
    private static readonly TimeSpan DoublePressWindow = TimeSpan.FromMilliseconds(450);

    /// <summary>Delay before reading the clipboard after the second press, so
    /// the source app has time to actually finish its copy.</summary>
    private static readonly TimeSpan ClipboardSettleDelay = TimeSpan.FromMilliseconds(120);

    private readonly LowLevelKeyboardHook _hook;
    private readonly Dispatcher _uiDispatcher;
    private readonly List<Action> _listeners = new();
    private readonly object _lock = new();

    private DateTime _lastCtrlCAt = DateTime.MinValue;
    private bool _ctrlDown;

    public DoubleCtrlCDetector(Dispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
        _hook = new LowLevelKeyboardHook();
        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
    }

    public int ListenerCount
    {
        get { lock (_lock) return _listeners.Count; }
    }

    /// <summary>
    /// Registers a callback for the double-press. Returns a disposable
    /// that unregisters when disposed (so callers can cleanly unsubscribe
    /// when a translator service is removed).
    /// </summary>
    public IDisposable Subscribe(Action callback)
    {
        lock (_lock)
        {
            _listeners.Add(callback);
            if (_listeners.Count == 1)
            {
                _hook.Install();
                Logger.Info("DoubleCtrlCDetector: hook installed (first listener).");
            }
        }
        return new Subscription(this, callback);
    }

    private void Unsubscribe(Action callback)
    {
        lock (_lock)
        {
            _listeners.Remove(callback);
            if (_listeners.Count == 0)
            {
                // Keep the hook installed: listeners come and go (e.g. when
                // editing a service) and reinstalling is a privileged op.
                // Disposal happens only when the app shuts down.
            }
        }
    }

    private void OnKeyDown(uint vk)
    {
        if (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL)
        {
            _ctrlDown = true;
            Logger.Debug($"[CC] Ctrl down (vk=0x{vk:X}) → ctrlDown=true");
            return;
        }

        if (vk == VK_C)
        {
            // Smoking-gun log: every C-down with the ctrlDown gate state.
            // If user reports Ctrl+C+C "not working" and we see C-down with
            // ctrlDown=false, the Ctrl-down event was dropped by Windows
            // (LowLevelHooksTimeout — UI thread was busy when Ctrl was hit).
            if (!_ctrlDown)
            {
                Logger.Debug($"[CC] C down but ctrlDown=false → IGNORED (Ctrl-down was missed by hook)");
                return;
            }

            var now = DateTime.UtcNow;
            var gap = now - _lastCtrlCAt;
            _lastCtrlCAt = now;
            Logger.Debug($"[CC] Ctrl+C registered, gap={gap.TotalMilliseconds:F0}ms (window≤{DoublePressWindow.TotalMilliseconds:F0}ms)");

            if (gap > TimeSpan.Zero && gap <= DoublePressWindow)
            {
                // Reset so a third C doesn't pair with the second.
                _lastCtrlCAt = DateTime.MinValue;
                Logger.Info($"[CC] DOUBLE Ctrl+C detected — firing {ListenerCount} listener(s).");
                FireDoublePress();
            }
        }
    }

    private void OnKeyUp(uint vk)
    {
        if (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL)
        {
            _ctrlDown = false;
            Logger.Debug($"[CC] Ctrl up (vk=0x{vk:X}) → ctrlDown=false");
        }
    }

    private void FireDoublePress()
    {
        Action[] snapshot;
        lock (_lock) snapshot = _listeners.ToArray();
        if (snapshot.Length == 0) return;

        // Marshal to UI thread + small delay so the second Ctrl+C has time
        // to land in the clipboard before listeners read it.
        _uiDispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(ClipboardSettleDelay);
            foreach (var cb in snapshot)
            {
                try { cb(); }
                catch (Exception ex) { Logger.Error("DoubleCtrlCDetector listener", ex); }
            }
        });
    }

    public void Dispose()
    {
        _hook.Dispose();
        lock (_lock) _listeners.Clear();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly DoubleCtrlCDetector _owner;
        private Action? _cb;
        public Subscription(DoubleCtrlCDetector owner, Action cb) { _owner = owner; _cb = cb; }
        public void Dispose()
        {
            if (_cb == null) return;
            _owner.Unsubscribe(_cb);
            _cb = null;
        }
    }
}
