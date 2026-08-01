using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WebViewHub.Services;

public class FaviconService
{
    private readonly string _iconsDir;
    private readonly HttpClient _http;
    private readonly MacOSIconsCache _macOSIconsCache;

    public FaviconService(string iconsDir, MacOSIconsCache macOSIconsCache)
    {
        _iconsDir = iconsDir;
        _macOSIconsCache = macOSIconsCache;
        Directory.CreateDirectory(iconsDir);
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebViewHub/1.0");
    }

    /// <summary>
    /// Returns a path to a PNG/ICO file for the given service.
    /// Tries macOSicons.com (curated app icons, requires API key in
    /// <see cref="Models.HubSettings.MacOSIconsApiKey"/>) first when a
    /// <paramref name="serviceName"/> is provided, then falls back through
    /// Google's favicon service, DuckDuckGo, parsed HTML hints, and the
    /// raw /favicon.ico. Returns null if nothing worked.
    /// </summary>
    public async Task<string?> GetIconAsync(string serviceId, string url, string? serviceName = null)
    {
        var iconPath = Path.Combine(_iconsDir, $"{serviceId}.png");

        if (File.Exists(iconPath))
        {
            if (await IsValidImageFileAsync(iconPath)) return iconPath;
            try { File.Delete(iconPath); } catch { /* ignore */ }
        }

        // Curated step — exactly one source attempted based on Hub-settings
        // selection. Independent of the favicon chain below, which ALWAYS
        // runs after a curated miss so services without a curated entry
        // still get an icon.
        var source = GetIconSource();
        Logger.Debug($"GetIconAsync({serviceId}, '{serviceName}'): IconSource={source}");
        switch (source)
        {
            case Models.IconSource.MacOSIcons:
                var apiKey = TryGetMacOSIconsApiKey();
                if (!string.IsNullOrWhiteSpace(serviceName) && !string.IsNullOrWhiteSpace(apiKey))
                {
                    if (await TryFromMacOSIconsAsync(serviceName!, apiKey!, iconPath))
                    {
                        Logger.Info($"Icon for '{serviceName}' fetched from macOSicons.");
                        return iconPath;
                    }
                }
                break;

            case Models.IconSource.WebCatalog:
                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    if (await TryFromWebCatalogAsync(serviceName!, iconPath))
                        return iconPath;
                }
                break;

            case Models.IconSource.Standard:
                // No curated step — drop straight to favicons.
                break;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        // 1. Google's favicon service — clean PNG, handles CDN/caching.
        var googleUrl = $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=128";
        if (await TryDownloadImageAsync(googleUrl, iconPath)) return iconPath;

        // 2. DuckDuckGo's favicon service — covers a different long tail.
        var ddgUrl = $"https://icons.duckduckgo.com/ip3/{uri.Host}.ico";
        if (await TryDownloadImageAsync(ddgUrl, iconPath)) return iconPath;

        // 3. Parse the site's HTML for <link rel="icon"> hints. Modern SPAs
        //    keep favicons on a hashed CDN path, not /favicon.ico.
        if (await TryFromHtmlAsync(uri, iconPath)) return iconPath;

        // 4. Last resort: direct /favicon.ico. SPAs that 200-OK with HTML
        //    on unknown paths are filtered by the magic-byte check.
        var directUrl = $"{uri.Scheme}://{uri.Host}/favicon.ico";
        if (await TryDownloadImageAsync(directUrl, iconPath)) return iconPath;

        return null;
    }

    /// <summary>
    /// Force a re-download (invalidates cached icon).
    /// </summary>
    public async Task<string?> RefreshIconAsync(string serviceId, string url, string? serviceName = null)
    {
        var iconPath = Path.Combine(_iconsDir, $"{serviceId}.png");
        if (File.Exists(iconPath))
        {
            try { File.Delete(iconPath); } catch { /* ignore */ }
        }
        return await GetIconAsync(serviceId, url, serviceName);
    }

    /// <summary>
    /// Reads the macOSicons key from the live config. Done at call time
    /// (not cached at ctor) so the user can paste a key in Hub settings
    /// and have the next icon-fetch immediately use it.
    /// </summary>
    private static string? TryGetMacOSIconsApiKey()
    {
        try { return App.Config?.Config?.HubSettings?.MacOSIconsApiKey; }
        catch { return null; }
    }

    /// <summary>
    /// Reads the Hub-settings curated source selection. Defaults to
    /// <see cref="Models.IconSource.MacOSIcons"/> on any access failure
    /// (matches the historical "always tries macOSicons first" behaviour).
    /// </summary>
    private static Models.IconSource GetIconSource()
    {
        try { return App.Config?.Config?.HubSettings?.IconSource ?? Models.IconSource.MacOSIcons; }
        catch { return Models.IconSource.MacOSIcons; }
    }

    /// <summary>
    /// POST to <c>api.macosicons.com/api/search</c>, take the top hit by
    /// downloads, download its PNG and save to <paramref name="destPath"/>.
    /// Returns false on any error so the caller falls back to favicons.
    /// </summary>
    /// <summary>
    /// Fetches a curated 256-px icon from webcatalog.io's CDN. The URL is
    /// deterministic — <c>cdn-1.webcatalog.io/catalog/{slug}/{slug}-icon-filled-256.webp</c>
    /// where slug is the lowercased, hyphenated service name. No API key,
    /// no rate limit observed. Returns false on any non-success status
    /// so the favicon fallback chain runs for services webcatalog doesn't
    /// have (404).
    /// </summary>
    private async Task<bool> TryFromWebCatalogAsync(string serviceName, string destPath)
    {
        var slug = SlugifyForWebCatalog(serviceName);
        if (string.IsNullOrEmpty(slug)) return false;

        var url = $"https://cdn-1.webcatalog.io/catalog/{slug}/{slug}-icon-filled-256.webp";
        Logger.Debug($"TryFromWebCatalog: '{serviceName}' → slug='{slug}' → {url}");
        if (await TryDownloadImageAsync(url, destPath))
        {
            Logger.Info($"Icon for '{serviceName}' fetched from webcatalog.io (slug='{slug}').");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reduces a free-form service name to a webcatalog.io URL slug —
    /// lowercased, [a-z0-9-] only, spaces/underscores → hyphens, no
    /// runs of hyphens, trimmed. Matches the observed pattern (e.g.
    /// "Pinterest" → "pinterest", "Google Drive" → "google-drive").
    /// </summary>
    private static string SlugifyForWebCatalog(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var lower = name.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            // anything else (punctuation, accents) → dropped
        }
        return Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
    }

    private async Task<bool> TryFromMacOSIconsAsync(string serviceName, string apiKey, string destPath)
    {
        try
        {
            var (hits, _, _) = await SearchMacOSIconsAsync(serviceName, hitsPerPage: 5);
            var hit = hits.FirstOrDefault();
            if (hit == null)
            {
                Logger.Info($"macOSicons no hits for '{serviceName}'");
                return false;
            }

            // Same priority chain as the manual picker — macOS-rendered
            // first (matches picker preview, looks polished in Hub tile)
            // and full-bleed iOS asset only as a last resort:
            //   icnsUrl (largest PNG frame) → lowResPngUrl (128px) → iOSUrl
            var candidates = new[] { hit.IcnsUrl, hit.LowResPngUrl, hit.IOSUrl }
                .Where(u => !string.IsNullOrEmpty(u))
                .Cast<string>()
                .ToList();
            if (candidates.Count == 0)
            {
                Logger.Info($"macOSicons hit has no source URL for '{serviceName}'");
                return false;
            }

            Logger.Debug($"macOSicons hit '{hit.AppName}' downloads={hit.Downloads} → trying {candidates.Count} candidate(s)");
            foreach (var cand in candidates)
            {
                if (await TryDownloadImageAsync(cand, destPath)) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"macOSicons request for '{serviceName}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Public search variant for the icon-picker UI: returns the full list
    /// of hits (not just the top one), the timestamp the cache was last
    /// refreshed from the API, and an optional error string so the picker
    /// can distinguish "no matches" from "rate-limited" / "auth failed".
    ///
    /// <para>Caching:</para> results are stored in
    /// <see cref="MacOSIconsCache"/> indefinitely. By default this method
    /// returns the cached entry when one exists and only hits the API on
    /// a cache miss. Pass <paramref name="forceRefresh"/>=true to skip the
    /// cache and re-fetch — wired to the Refresh button in the picker.
    /// This matters because the free macOSicons tier is ~50 requests/month
    /// and a single picker open already triggers a search.
    /// </summary>
    public async Task<(List<MacOSIconsHit> hits, DateTime? cachedAtUtc, string? error)> SearchMacOSIconsAsync(
        string query,
        int hitsPerPage = 20,
        bool forceRefresh = false)
    {
        var apiKey = TryGetMacOSIconsApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return (new List<MacOSIconsHit>(), null, "No macOSicons API key in Hub settings.");
        if (string.IsNullOrWhiteSpace(query))
            return (new List<MacOSIconsHit>(), null, null);

        // Try cache first unless caller forced a refresh.
        if (!forceRefresh)
        {
            var cached = _macOSIconsCache.Get(query);
            if (cached != null)
            {
                Logger.Debug($"macOSicons cache HIT '{query}' ({cached.Hits.Count} hits, cached {cached.CachedAtUtc:O})");
                return (cached.Hits, cached.CachedAtUtc, null);
            }
        }

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.macosicons.com/api/search")
            {
                Content = JsonContent.Create(new
                {
                    query,
                    searchOptions = new
                    {
                        hitsPerPage,
                        sort = new[] { "downloads:desc" },
                    }
                })
            };
            req.Headers.Add("x-api-key", apiKey);
            Logger.Info($"macOSicons API call for '{query}' (forceRefresh={forceRefresh})");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"macOSicons search {(int)resp.StatusCode} for '{query}'");
                var msg = (int)resp.StatusCode switch
                {
                    429 => "macOSicons rate limit hit. Wait a few minutes and try again.",
                    401 or 403 => "macOSicons rejected the API key. Check Hub settings → Icons.",
                    500 or 502 or 503 or 504 => "macOSicons is temporarily unavailable. Try again in a moment.",
                    _ => $"macOSicons returned HTTP {(int)resp.StatusCode}.",
                };
                return (new List<MacOSIconsHit>(), null, msg);
            }
            var payload = await resp.Content.ReadFromJsonAsync<MacOSIconsResponse>();
            var hits = payload?.Hits ?? new List<MacOSIconsHit>();
            // Cache even an empty list — re-searching a niche query
            // would otherwise burn another API call for the same null
            // result. User can press Refresh if they want to retry.
            _macOSIconsCache.Put(query, hits);
            return (hits, DateTime.UtcNow, null);
        }
        catch (Exception ex)
        {
            Logger.Warn($"macOSicons search '{query}' failed: {ex.Message}");
            return (new List<MacOSIconsHit>(), null, $"Search failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces the cached icon for a service with the first PNG URL in
    /// <paramref name="candidateUrls"/> that downloads successfully.
    /// Used by the manual icon picker: callers pass [iOSUrl, lowResPngUrl]
    /// so a hit whose 1024px source 403s falls back to the always-present
    /// 128px macOS-rendered preview.
    /// </summary>
    public async Task<bool> ReplaceIconAsync(string serviceId, params string?[] candidateUrls)
    {
        var iconPath = Path.Combine(_iconsDir, $"{serviceId}.png");
        foreach (var url in candidateUrls)
        {
            if (string.IsNullOrEmpty(url)) continue;
            if (await TryDownloadImageAsync(url, iconPath))
            {
                Logger.Info($"ReplaceIconAsync({serviceId}) ← {url} → ok");
                return true;
            }
            Logger.Info($"ReplaceIconAsync({serviceId}) ← {url} → fail, trying next…");
        }
        Logger.Warn($"ReplaceIconAsync({serviceId}) → all {candidateUrls.Length} candidate URL(s) failed");
        return false;
    }

    private async Task<bool> TryFromHtmlAsync(Uri siteUri, string destPath)
    {
        try
        {
            var rootUrl = $"{siteUri.Scheme}://{siteUri.Host}/";
            using var resp = await _http.GetAsync(rootUrl);
            if (!resp.IsSuccessStatusCode) return false;

            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!ctype.Contains("html", StringComparison.OrdinalIgnoreCase)) return false;

            var html = await resp.Content.ReadAsStringAsync();
            foreach (var iconUrl in ExtractIconUrls(html, siteUri))
            {
                if (await TryDownloadImageAsync(iconUrl, destPath)) return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    private static IEnumerable<string> ExtractIconUrls(string html, Uri siteUri)
    {
        var linkTags = Regex.Matches(
            html,
            @"<link\b[^>]*?>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var candidates = new List<(int rank, string url)>();
        foreach (Match m in linkTags)
        {
            var tag = m.Value;

            var relMatch = Regex.Match(tag, @"\brel\s*=\s*(?:""(?<rel>[^""]*)""|'(?<rel>[^']*)'|(?<rel>[^\s>]+))", RegexOptions.IgnoreCase);
            if (!relMatch.Success) continue;
            var rel = relMatch.Groups["rel"].Value;
            if (!rel.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;

            var hrefMatch = Regex.Match(tag, @"\bhref\s*=\s*(?:""(?<href>[^""]*)""|'(?<href>[^']*)'|(?<href>[^\s>]+))", RegexOptions.IgnoreCase);
            if (!hrefMatch.Success) continue;

            var href = hrefMatch.Groups["href"].Value;
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(siteUri, href, out var abs)) continue;

            int size = 0;
            var sizesMatch = Regex.Match(tag, @"\bsizes\s*=\s*(?:""(?<s>[^""]*)""|'(?<s>[^']*)'|(?<s>[^\s>]+))", RegexOptions.IgnoreCase);
            if (sizesMatch.Success)
            {
                foreach (Match dim in Regex.Matches(sizesMatch.Groups["s"].Value, @"(\d+)\s*x", RegexOptions.IgnoreCase))
                {
                    if (int.TryParse(dim.Groups[1].Value, out var n) && n > size) size = n;
                }
            }

            int rank = size;
            if (rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase)) rank += 1000;

            candidates.Add((rank, abs.ToString()));
        }

        return candidates
            .OrderByDescending(c => c.rank)
            .Select(c => c.url)
            .Distinct();
    }

    private async Task<bool> TryDownloadImageAsync(string url, string destPath)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            // macOSicons S3 sometimes requires the website Referer to
            // serve thumbnails; harmless for other CDNs.
            if (url.Contains("macosicons", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Referrer = new Uri("https://macosicons.com/");
            }
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;

            // ctype check: image/* AND application/octet-stream AND
            // .icns container (served as application/octet-stream or
            // a custom type). Don't reject too aggressively — the magic
            // byte check below is the real filter.
            var bytes = await resp.Content.ReadAsByteArrayAsync();

            // ICNS container: extract the largest PNG frame and use that
            // as the source. Many macOSicons hits have a working
            // s3.macosicons.com .icns even when their iOSUrl points at
            // the dead parsefiles.back4app.com bucket — going via .icns
            // gives us the high-res frame we want for crisp taskbar/tray.
            if (WebViewHub.Helpers.IcnsParser.IsIcns(bytes))
            {
                var extracted = WebViewHub.Helpers.IcnsParser.TryExtractLargestPng(bytes);
                if (extracted == null)
                {
                    Logger.Warn($"TryDownloadImageAsync: '{url}' is .icns but no PNG frame extracted.");
                    return false;
                }
                Logger.Debug($"TryDownloadImageAsync: .icns → extracted {extracted.Length}-byte PNG frame from {url}");
                bytes = extracted;
            }

            // WebP — convert to PNG via WIC so NormalizeIcon's GDI+ pipe
            // works regardless of OS webp codec availability. Webcatalog.io
            // serves all icons as webp; some macOSicons hits also do.
            if (WebViewHub.Helpers.IconHelper.IsWebP(bytes))
            {
                var converted = WebViewHub.Helpers.IconHelper.ConvertImageToPngBytes(bytes);
                if (converted == null)
                {
                    Logger.Warn($"TryDownloadImageAsync: webp → PNG conversion failed for {url}");
                    return false;
                }
                Logger.Debug($"TryDownloadImageAsync: webp → {converted.Length}-byte PNG from {url}");
                bytes = converted;
            }

            if (!IsImageBytes(bytes)) return false;

            // Canonical normalization (auto-crop transparent borders,
            // aspect-fit into 90% of canvas, mask through the bundled
            // macOS-squircle PNG). Same pipeline as rikumi/iconsur.
            // After this, the on-disk file is a uniform 1024×1024 PNG
            // regardless of whether the source was iOSUrl, lowResPngUrl,
            // an extracted .icns frame, or a raw favicon — so the Hub
            // tile, tray icon, taskbar, and .ico generator all see the
            // same canonical shape and size.
            var normalized = WebViewHub.Helpers.IconHelper.NormalizeIcon(bytes);
            await File.WriteAllBytesAsync(destPath, normalized);

            // Multi-frame .ico generation is now handled on-demand by
            // IconHelper.EnsureIcoFile (called from ResolveIconResource
            // when the service window opens). One generator, one set of
            // sizes — see IconHelper.IcoFrameSizes.
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsValidImageFileAsync(string path)
    {
        try
        {
            var buf = new byte[16];
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var n = await fs.ReadAsync(buf.AsMemory(0, buf.Length));
            if (n < buf.Length) return false;
            return IsImageBytes(buf);
        }
        catch { return false; }
    }

    /// <summary>
    /// Confirms the payload is a raster image WPF can render (PNG/JPEG/GIF/
    /// ICO/BMP). SVG and WebP are intentionally rejected: WPF's BitmapImage
    /// can't load them, so caching one would just produce a broken icon and
    /// block all the fallback providers from running again.
    /// </summary>
    private static bool IsImageBytes(byte[] bytes)
    {
        if (bytes.Length < 16) return false;

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;  // PNG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;                      // JPEG
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) return true;  // GIF8
        if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00) return true;  // ICO
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return true;                                          // BMP

        return false;
    }

    private sealed class MacOSIconsResponse
    {
        [JsonPropertyName("hits")]
        public List<MacOSIconsHit>? Hits { get; set; }
    }
}

/// <summary>
/// One search result from <c>api.macosicons.com/api/search</c>. Exposed
/// publicly so the icon-picker UI can data-bind to it directly.
/// </summary>
public sealed class MacOSIconsHit
{
    [JsonPropertyName("appName")]      public string? AppName { get; set; }
    [JsonPropertyName("icnsUrl")]      public string? IcnsUrl { get; set; }
    [JsonPropertyName("iOSUrl")]       public string? IOSUrl { get; set; }
    [JsonPropertyName("lowResPngUrl")] public string? LowResPngUrl { get; set; }
    [JsonPropertyName("downloads")]    public int Downloads { get; set; }
    [JsonPropertyName("credit")]       public string? Credit { get; set; }
    [JsonPropertyName("uploadedBy")]   public string? UploadedBy { get; set; }
}
