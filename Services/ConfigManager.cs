using System.IO;
using System.Text.Json;
using WebViewHub.Models;

namespace WebViewHub.Services;

public class ConfigManager
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializes concurrent saves. Fire-and-forget callers (e.g.
    /// ServiceWindow.OnClosing) MUST NOT race the file because the
    /// previous design (Delete + Move) had a window where config.json
    /// did not exist on disk — process termination there nuked the file.
    /// </summary>
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AppConfig Config { get; private set; } = new();

    public ConfigManager(string path) => _path = path;

    public async Task LoadAsync()
    {
        if (!File.Exists(_path))
        {
            Config = new AppConfig();
            SaveSync();
            return;
        }

        try
        {
            // Scoped explicitly: SaveSync below replaces the file via
            // File.Move, which fails with UnauthorizedAccessException while
            // this read handle is still open.
            await using (var fs = File.OpenRead(_path))
            {
                var loaded = await JsonSerializer.DeserializeAsync<AppConfig>(fs, JsonOpts);
                Config = loaded ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            // Corrupted config — back it up and start fresh, but log loudly.
            Logger.Error($"Config deserialize failed for {_path} — backing up and starting fresh.", ex);
            try
            {
                var backup = _path + ".broken." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(_path, backup, overwrite: true);
            }
            catch { /* best effort */ }
            Config = new AppConfig();
            SaveSync();
            return;
        }

        // Multi-profile migration — runs only after the read handle above is
        // released. Services written before profiles existed get one
        // "Default" entry whose ProfileKey is the service Id, i.e. the same
        // WebView2 bucket they already use, so no session is disturbed.
        // Idempotent on already-migrated files.
        //
        // Persisted immediately because ServiceProfile.Id is generated fresh
        // on each call: leaving the result in memory would hand out a
        // different ActiveProfileId on every launch until something else
        // happened to save.
        var migrated = Config.Services.Count(s => s.Profiles.Count == 0);
        foreach (var svc in Config.Services) svc.EnsureProfiles();
        if (migrated > 0)
        {
            Logger.Info($"Config migration: created default profile for {migrated} service(s).");
            SaveSync();
        }
    }

    /// <summary>
    /// Async wrapper around the sync save so existing await-callers still
    /// compile. Keeps the same atomic-replace semantics.
    /// </summary>
    public Task SaveAsync() => Task.Run(SaveSync);

    /// <summary>
    /// Atomic, synchronous save. Use this from any context where you
    /// can't reliably await (window close, app shutdown, etc.) so the
    /// write completes before the process exits.
    ///
    /// Atomicity is guaranteed by File.Move(overwrite: true) which on
    /// NTFS uses MoveFileEx with MOVEFILE_REPLACE_EXISTING — config.json
    /// is replaced as one operation, never goes missing in between.
    /// </summary>
    public void SaveSync()
    {
        _saveLock.Wait();
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);

            // Use a per-process tmp suffix so concurrent saves on the
            // SAME path don't fight over the same .tmp file.
            var tmp = _path + ".tmp." + Environment.CurrentManagedThreadId;
            var json = JsonSerializer.Serialize(Config, JsonOpts);
            File.WriteAllText(tmp, json);

            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error($"SaveSync failed for {_path}", ex);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public ServiceConfig? FindByName(string name) =>
        Config.Services.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public ServiceConfig? FindById(string id) =>
        Config.Services.FirstOrDefault(s => s.Id == id);
}
