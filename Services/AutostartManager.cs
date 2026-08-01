using System.Diagnostics;
using Microsoft.Win32;

namespace WebViewHub.Services;

/// <summary>
/// Manages per-service entries under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
///
/// HKCU (current user) means no admin rights are needed and the entries
/// stay portable to whichever user runs the app.
/// </summary>
public static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ValueName(string serviceId) => $"WebViewHub.{serviceId}";

    public static void Apply(string serviceId, string serviceArgValue, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("Cannot open HKCU Run key");

        var name = ValueName(serviceId);

        if (!enabled)
        {
            try { key.DeleteValue(name, throwOnMissingValue: false); } catch { }
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName
                      ?? System.IO.Path.Combine(AppContext.BaseDirectory, "WebViewHub.exe");
        var command = $"\"{exePath}\" --service=\"{serviceArgValue}\"";
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public static void Remove(string serviceId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName(serviceId), throwOnMissingValue: false);
        }
        catch { /* nothing useful to do */ }
    }

    public static bool IsEnabled(string serviceId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName(serviceId)) != null;
        }
        catch { return false; }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Hub autostart — separate value name from any per-service entry
    // ────────────────────────────────────────────────────────────────────

    private const string HubValueName = "WebViewHub.Hub";

    /// <summary>
    /// Writes (or removes) the HKCU\Run entry that launches WebViewHub at
    /// Windows sign-in. When <paramref name="startHidden"/> is true the
    /// command line includes <c>--start-hidden</c> so App.OnStartup keeps
    /// the Hub window invisible and only the tray icon is shown.
    /// </summary>
    public static void ApplyHub(bool enabled, bool startHidden)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("Cannot open HKCU Run key");

        if (!enabled)
        {
            try { key.DeleteValue(HubValueName, throwOnMissingValue: false); } catch { }
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName
                      ?? System.IO.Path.Combine(AppContext.BaseDirectory, "WebViewHub.exe");
        var command = startHidden
            ? $"\"{exePath}\" --start-hidden"
            : $"\"{exePath}\"";
        key.SetValue(HubValueName, command, RegistryValueKind.String);
    }
}
