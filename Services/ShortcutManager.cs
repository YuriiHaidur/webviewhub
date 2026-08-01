using System.Diagnostics;
using System.IO;
using WebViewHub.Helpers;

namespace WebViewHub.Services;

/// <summary>
/// Creates and removes per-service .lnk shortcuts in
/// %APPDATA%\Microsoft\Windows\Start Menu\Programs\WebViewHub\.
/// Windows Start search and tools like Raycast index this folder, so a
/// shortcut here makes the service findable by its name.
///
/// We use the WScript.Shell COM (built into Windows since forever) instead
/// of P/Invoking IShellLink — much shorter, same effect.
/// </summary>
public static class ShortcutManager
{
    public const string HubShortcutFileName = "WebViewHub.lnk";
    public const string HubAumid = "WebViewHub.Hub";

    public static string FolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "WebViewHub");

    public static string GetShortcutPath(string serviceName) =>
        Path.Combine(FolderPath, $"{Sanitize(serviceName)}.lnk");

    public static string GetHubShortcutPath() =>
        Path.Combine(FolderPath, HubShortcutFileName);

    public static void Create(string serviceName, string serviceArgValue, string? iconPath, string? aumid = null)
    {
        WriteShortcut(
            shortcutPath: GetShortcutPath(serviceName),
            arguments: $"--service=\"{serviceArgValue}\"",
            description: $"WebViewHub — {serviceName}",
            iconPath: iconPath,
            aumid: aumid);
    }

    /// <summary>
    /// Creates / overwrites the top-level "WebViewHub.lnk" that opens the
    /// hub itself (no --service argument). Refreshed on every startup so a
    /// move of the install folder repoints it at the new exe path, same as
    /// the per-service shortcuts.
    /// </summary>
    public static void CreateHub()
    {
        var exePath = GetCurrentExePath();
        WriteShortcut(
            shortcutPath: GetHubShortcutPath(),
            arguments: "",
            description: "WebViewHub",
            // The exe carries its own resource icon — use it directly so we
            // don't depend on a cached favicon for the hub.
            iconPath: exePath,
            aumid: HubAumid);
    }

    /// <summary>
    /// Removes the Hub Start-menu shortcut. Idempotent — silent no-op when
    /// the .lnk is already gone.
    /// </summary>
    public static void RemoveHub()
    {
        var p = GetHubShortcutPath();
        try { if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) { Logger.Warn($"RemoveHub failed: {ex.Message}"); }
    }

    private static void WriteShortcut(string shortcutPath, string arguments, string description, string? iconPath, string? aumid)
    {
        Directory.CreateDirectory(FolderPath);

        var exePath = GetCurrentExePath();
        var workDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell COM is unavailable");
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell is null) throw new InvalidOperationException("Failed to create WScript.Shell instance");

        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = exePath;
                shortcut.Arguments = arguments;
                shortcut.WorkingDirectory = workDir;
                shortcut.Description = description;
                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                {
                    shortcut.IconLocation = iconPath + ",0";
                }
                shortcut.Save();
            }
            finally
            {
                if (System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }

        // Bake AUMID into the saved .lnk so a pinned copy in the taskbar
        // shares identity with the running window — avoids the duplicate
        // "ghost icon" problem when the user pins this shortcut and then
        // launches the target.
        if (!string.IsNullOrEmpty(aumid))
        {
            NativeMethods.SetShortcutAppUserModelId(shortcutPath, aumid);
        }
    }

    private static string GetCurrentExePath() =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "WebViewHub.exe");

    public static void Remove(string serviceName)
    {
        var p = GetShortcutPath(serviceName);
        try { if (File.Exists(p)) File.Delete(p); }
        catch { /* ignore — user can delete manually */ }
    }

    /// <summary>
    /// Removes a shortcut whose filename matches the *previous* service name.
    /// Called when a user renames a service so we don't leave a stale .lnk.
    /// </summary>
    public static void RemoveByName(string oldName)
    {
        if (string.IsNullOrEmpty(oldName)) return;
        Remove(oldName);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "service" : s;
    }
}
