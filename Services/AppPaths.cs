using System.IO;

namespace WebViewHub.Services;

/// <summary>
/// Resolves where to put the data folder.
/// Prefers a "Data" folder next to the .exe (portable mode).
/// Falls back to %APPDATA%\WebViewHub if the exe folder is read-only
/// (typical when installed under Program Files).
/// </summary>
public class AppPaths
{
    public string DataDir { get; }
    public string ConfigFile { get; }
    public string IconsDir { get; }
    public string ProfilesDir { get; }
    public string WebView2DataDir { get; }
    /// <summary>JSONL log of per-service usage events (open / focus / close).
    /// Append-only, read by the Stats window to build per-day aggregates.</summary>
    public string UsageLogFile { get; }
    public bool IsPortable { get; }

    private AppPaths(string dataDir, bool isPortable)
    {
        DataDir = dataDir;
        IsPortable = isPortable;
        ConfigFile = Path.Combine(dataDir, "config.json");
        IconsDir = Path.Combine(dataDir, "icons");
        ProfilesDir = Path.Combine(dataDir, "profiles");
        WebView2DataDir = Path.Combine(dataDir, "webview2");
        UsageLogFile = Path.Combine(dataDir, "usage.jsonl");

        Directory.CreateDirectory(IconsDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(WebView2DataDir);
    }

    public static AppPaths Resolve()
    {
        var exeDir = AppContext.BaseDirectory;
        var portablePath = Path.Combine(exeDir, "Data");

        if (TryEnsureWritable(portablePath))
        {
            return new AppPaths(portablePath, isPortable: true);
        }

        // Fallback: portable folder is not writable (most likely the app
        // is installed under Program Files). Use %APPDATA% instead.
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WebViewHub");

        Directory.CreateDirectory(appDataPath);
        return new AppPaths(appDataPath, isPortable: false);
    }

    private static bool TryEnsureWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var testFile = Path.Combine(path, ".write_test");
            File.WriteAllText(testFile, "");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
