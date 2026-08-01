namespace WebViewHub.Services;

/// <summary>
/// Builds the Google Translate web URL for the new (m=) UI:
///   https://translate.google.com/?sl=auto&tl=ru&text=...&op=translate
///
/// Source is always auto-detected; target language is configurable per
/// service. Empty/missing text falls back to opening the bare translator.
/// </summary>
public static class TranslatorUrl
{
    private const string Base = "https://translate.google.com/";
    public const string DefaultTargetLang = "ru";

    /// <summary>Maximum encoded text length we put in the URL.
    /// Browsers and the Google endpoint both tolerate ~5K chars; we
    /// stay well below that to avoid 414 (URI Too Long).</summary>
    private const int MaxTextLength = 4000;

    public static string Build(string? targetLang, string? text)
    {
        var tl = NormalizeLang(targetLang);
        var encoded = EncodeForUrl(text);
        if (string.IsNullOrEmpty(encoded))
        {
            return $"{Base}?sl=auto&tl={tl}&op=translate";
        }
        return $"{Base}?sl=auto&tl={tl}&text={encoded}&op=translate";
    }

    private static string NormalizeLang(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return DefaultTargetLang;
        return lang.Trim().ToLowerInvariant();
    }

    private static string EncodeForUrl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var trimmed = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        return Uri.EscapeDataString(trimmed);
    }
}
