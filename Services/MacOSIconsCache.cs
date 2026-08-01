using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WebViewHub.Services;

/// <summary>
/// File-backed cache for macOSicons search results. Free-tier API allows
/// only ~50 requests/month, so we keep results indefinitely on disk and
/// only re-hit the API when the user explicitly presses Refresh in the
/// picker. Each query maps to one JSON file in <c>Data/macosicons-cache/</c>.
/// </summary>
public class MacOSIconsCache
{
    private readonly string _dir;

    public MacOSIconsCache(string dataDir)
    {
        _dir = Path.Combine(dataDir, "macosicons-cache");
        Directory.CreateDirectory(_dir);
    }

    public CacheEntry? Get(string query)
    {
        var path = PathFor(query);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CacheEntry>(json);
        }
        catch (Exception ex)
        {
            Logger.Warn($"MacOSIconsCache.Get('{query}') failed: {ex.Message}");
            return null;
        }
    }

    public void Put(string query, List<MacOSIconsHit> hits)
    {
        var entry = new CacheEntry
        {
            Query = query,
            CachedAtUtc = DateTime.UtcNow,
            Hits = hits,
        };
        try
        {
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
            var path = PathFor(query);
            File.WriteAllText(path, json);
            Logger.Debug($"MacOSIconsCache saved '{query}' ({hits.Count} hits) → {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"MacOSIconsCache.Put('{query}') failed: {ex.Message}");
        }
    }

    public void Invalidate(string query)
    {
        var path = PathFor(query);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Logger.Warn($"MacOSIconsCache.Invalidate('{query}') failed: {ex.Message}"); }
    }

    private string PathFor(string query)
    {
        var slug = SlugifyQuery(query);
        return Path.Combine(_dir, slug + ".json");
    }

    private static string SlugifyQuery(string query)
    {
        var lower = (query ?? "").Trim().ToLowerInvariant();
        // Strip path-invalid chars; map runs of whitespace/punct to a
        // single underscore so "Google Translate" and "google_translate"
        // both end up at the same key.
        var safe = Regex.Replace(lower, @"[^a-z0-9\-]+", "_").Trim('_');
        if (string.IsNullOrEmpty(safe)) safe = "empty";
        // Cap length so filename stays portable; collisions extremely
        // unlikely for human-typed queries.
        if (safe.Length > 80) safe = safe[..80];
        return safe;
    }

    public class CacheEntry
    {
        [JsonPropertyName("query")]       public string Query { get; set; } = "";
        [JsonPropertyName("cachedAtUtc")] public DateTime CachedAtUtc { get; set; }
        [JsonPropertyName("hits")]        public List<MacOSIconsHit> Hits { get; set; } = new();
    }
}
