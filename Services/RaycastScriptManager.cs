using System.Diagnostics;
using System.IO;
using System.Text;
using WebViewHub.Models;

namespace WebViewHub.Services;

/// <summary>
/// Generates one Raycast Script Command (.ps1) per configured service into
/// a known watched directory. Each script is a tiny PowerShell launcher that
/// invokes "WebViewHub.exe --service=&lt;name&gt;" with the absolute exe
/// path baked in — so the scripts survive Raycast restarts, OS shell quirks,
/// and don't depend on any URL-protocol registration.
///
/// Sync is idempotent: it overwrites existing files (refreshes the exe path
/// after a relocate) and prunes any "WebViewHub - *.ps1" file that no longer
/// matches a current service.
/// </summary>
public static class RaycastScriptManager
{
    /// <summary>
    /// User-chosen Raycast script directory. The user is expected to have
    /// added this path in Raycast → Settings → Script Commands → Add Script
    /// Directory. Hard-coded for now; promote to AppConfig if it ever needs
    /// to vary per machine.
    /// </summary>
    public const string ScriptsDir =
        @"E:\Programs\_Scripts\Raycast\WebViewHub";

    /// <summary>
    /// Where our scripts used to live (the parent of <see cref="ScriptsDir"/>).
    /// On every Sync we sweep "WebViewHub - *.ps1" and the matching icons
    /// folder out of this directory so that even if Raycast still indexes
    /// the parent, no stale duplicates show up next to the new ones.
    /// </summary>
    private static readonly string LegacyScriptsDir =
        Path.GetDirectoryName(ScriptsDir) ?? ScriptsDir;

    /// <summary>
    /// Filename prefix that uniquely identifies our scripts. Used both when
    /// writing and when pruning so we never touch unrelated user scripts in
    /// the same directory.
    /// </summary>
    private const string FilePrefix = "WebViewHub - ";

    /// <summary>
    /// Subfolder for per-service icons referenced from the .ps1 metadata.
    /// Raycast resolves @raycast.icon paths relative to the script's own
    /// directory, so this stays a sibling of the .ps1 files.
    /// </summary>
    private const string IconsSubDir = "webviewhub-icons";

    public static string GetScriptPath(string serviceName) =>
        Path.Combine(ScriptsDir, $"{FilePrefix}{Sanitize(serviceName)}.ps1");

    /// <summary>
    /// Rewrites every script for the current service list and prunes orphans.
    /// Best-effort: if the directory is on a drive that's not present, logs
    /// a warning and returns without throwing.
    /// </summary>
    public static void Sync(IList<ServiceConfig> services, string iconsSourceDir)
    {
        try
        {
            Directory.CreateDirectory(ScriptsDir);
            Directory.CreateDirectory(Path.Combine(ScriptsDir, IconsSubDir));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Raycast scripts dir unavailable ({ScriptsDir}): {ex.Message}");
            return;
        }

        var exePath = GetCurrentExePath();
        var written = 0;
        foreach (var svc in services)
        {
            try
            {
                WriteOne(svc, exePath, iconsSourceDir);
                written++;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to write Raycast script for '{svc.Name}': {ex.Message}");
            }
        }

        try { PruneOrphans(services); }
        catch (Exception ex) { Logger.Warn($"Raycast scripts prune failed: {ex.Message}"); }

        try { PruneLegacyLocation(); }
        catch (Exception ex) { Logger.Warn($"Raycast legacy-dir prune failed: {ex.Message}"); }

        Logger.Info($"Raycast scripts synced: {written}/{services.Count} script(s) at {ScriptsDir}");
    }

    /// <summary>
    /// Removes any "WebViewHub - *.ps1" + icon folder left behind in the
    /// directory above ScriptsDir from earlier versions of the app. This
    /// kept producing duplicate Spotify/Slack/etc. entries in Raycast
    /// because the launcher indexed both paths.
    /// </summary>
    private static void PruneLegacyLocation()
    {
        if (string.Equals(LegacyScriptsDir, ScriptsDir, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(LegacyScriptsDir)) return;

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(LegacyScriptsDir, $"{FilePrefix}*.ps1", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to delete legacy Raycast script '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        // The matching icons subfolder. Only delete if it still has only
        // our PNGs in it — never blow away an unrelated user folder that
        // happens to share the name.
        var legacyIcons = Path.Combine(LegacyScriptsDir, IconsSubDir);
        if (Directory.Exists(legacyIcons))
        {
            try
            {
                var unexpected = Directory.EnumerateFileSystemEntries(legacyIcons)
                    .Any(p => !p.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                if (!unexpected)
                {
                    Directory.Delete(legacyIcons, recursive: true);
                }
                else
                {
                    Logger.Info($"Legacy icons dir '{legacyIcons}' has non-png contents; leaving it alone.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to delete legacy icons dir '{legacyIcons}': {ex.Message}");
            }
        }

        if (removed > 0) Logger.Info($"Pruned {removed} legacy Raycast script(s) from {LegacyScriptsDir}");
    }

    /// <summary>
    /// Removes the script for a single service by its (current or previous)
    /// display name. Used during rename so the old file disappears before
    /// the new one is written.
    /// </summary>
    public static void Remove(string serviceName)
    {
        if (string.IsNullOrEmpty(serviceName)) return;
        var p = GetScriptPath(serviceName);
        try { if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) { Logger.Warn($"Failed to delete Raycast script '{p}': {ex.Message}"); }
    }

    private static void WriteOne(ServiceConfig svc, string exePath, string iconsSourceDir)
    {
        // Copy the cached favicon next to the scripts so Raycast can render
        // it in Root Search. Falls back to no icon if we never fetched one.
        string? iconRel = null;
        var sourceIcon = Path.Combine(iconsSourceDir, $"{svc.Id}.png");
        if (File.Exists(sourceIcon))
        {
            var targetIcon = Path.Combine(ScriptsDir, IconsSubDir, $"{svc.Id}.png");
            try
            {
                File.Copy(sourceIcon, targetIcon, overwrite: true);
                iconRel = $"{IconsSubDir}/{svc.Id}.png";
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to copy Raycast icon for '{svc.Name}': {ex.Message}");
            }
        }

        // Single-quoted PS string for the service name keeps '$', '`', '&'
        // literal. Escape embedded apostrophes by doubling them ('' = ').
        var safeName = svc.Name.Replace("'", "''");

        var lines = new List<string>
        {
            "#!/usr/bin/env pwsh",
            "",
            "# @raycast.schemaVersion 1",
            $"# @raycast.title {svc.Name}",
            "# @raycast.mode silent",
            "# @raycast.packageName WebViewHub",
        };
        if (iconRel != null) lines.Add($"# @raycast.icon {iconRel}");
        lines.Add($"# @raycast.description Open {svc.Name} in WebViewHub ({svc.Url})");
        lines.Add("");
        lines.Add($"& \"{exePath}\" --service='{safeName}'");
        lines.Add("");

        // No BOM — Raycast's Windows parser treats the leading BOM bytes as
        // part of the first line, which makes the @raycast.schemaVersion
        // marker fail to register and the script never appears. LF line
        // endings to match the format Raycast's own sample scripts ship in.
        var content = string.Join("\n", lines);
        File.WriteAllText(GetScriptPath(svc.Name), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void PruneOrphans(IList<ServiceConfig> services)
    {
        if (!Directory.Exists(ScriptsDir)) return;

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in services) keep.Add(Path.GetFileName(GetScriptPath(s.Name)));

        foreach (var path in Directory.EnumerateFiles(ScriptsDir, $"{FilePrefix}*.ps1", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (keep.Contains(name)) continue;
            try
            {
                File.Delete(path);
                Logger.Info($"Removed orphan Raycast script: {name}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to delete orphan Raycast script '{name}': {ex.Message}");
            }
        }
    }

    private static string GetCurrentExePath() =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "WebViewHub.exe");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "service" : s;
    }
}
