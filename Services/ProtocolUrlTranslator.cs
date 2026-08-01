namespace WebViewHub.Services;

/// <summary>
/// Translates a custom-scheme URI (e.g. "spotify:track:abc",
/// "spotify://playlist/xyz") into a web URL based on the owning
/// service's configured base URL, so the embedded WebView2 can load it.
///
/// Rules:
///  - "scheme:A:B"          → baseUrl + "/A/B"   (Spotify-style URI)
///  - "scheme://host/path"  → baseUrl + "/path"  (URL-style — drop host,
///                                                use the service base)
///  - "scheme://A"          → baseUrl + "/A"
///  - anything else / on failure → just baseUrl
///
/// We deliberately drop the host component of URL-style inputs because
/// the user picked the base URL when they configured the service; deep
/// links should land on the same host they picked.
/// </summary>
public static class ProtocolUrlTranslator
{
    public static string Translate(string scheme, string raw, string baseUrl)
    {
        if (string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(baseUrl))
            return baseUrl ?? "";

        // Strip optional surrounding quotes (some launchers wrap args).
        raw = (raw ?? "").Trim().Trim('"');

        var prefixWithSlashes = scheme + "://";
        var prefixColon = scheme + ":";

        string remainder;
        if (raw.StartsWith(prefixWithSlashes, StringComparison.OrdinalIgnoreCase))
        {
            // URL-style: drop "scheme://" and the host component too.
            // Example: "spotify://open.spotify.com/track/abc" → "track/abc".
            var afterScheme = raw[prefixWithSlashes.Length..];
            var firstSlash = afterScheme.IndexOf('/');
            remainder = firstSlash >= 0 ? afterScheme[(firstSlash + 1)..] : "";
        }
        else if (raw.StartsWith(prefixColon, StringComparison.OrdinalIgnoreCase))
        {
            // URI-style: replace remaining colons with slashes.
            // Example: "spotify:track:abc" → "track/abc".
            remainder = raw[prefixColon.Length..].Replace(':', '/');
        }
        else
        {
            return baseUrl;
        }

        if (string.IsNullOrEmpty(remainder)) return baseUrl;

        // Strip leading slashes from remainder, trailing slash from base,
        // join with one slash. Preserve query string if any.
        var trimmedBase = baseUrl.TrimEnd('/');
        var trimmedRemainder = remainder.TrimStart('/');
        return $"{trimmedBase}/{trimmedRemainder}";
    }

    /// <summary>
    /// True when the argument looks like a custom-scheme URI we should
    /// route to a service rather than treat as a normal CLI flag.
    /// </summary>
    public static bool LooksLikeProtocolArg(string arg, out string scheme)
    {
        scheme = "";
        if (string.IsNullOrEmpty(arg) || arg.StartsWith("--")) return false;

        var colon = arg.IndexOf(':');
        if (colon <= 0) return false;

        var s = arg[..colon];
        // Filter out absolute Windows paths ("C:\..."), drive letters, etc.
        if (s.Length == 1) return false;
        if (s.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("file", StringComparison.OrdinalIgnoreCase))
            return false;

        // Schemes are letters/digits/+/-/.
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '.'))
                return false;
        }

        scheme = s.ToLowerInvariant();
        return true;
    }
}
