using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WebViewHub.Models;
using WebViewHub.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ImageSource = System.Windows.Media.ImageSource;
using IconSource = WebViewHub.Models.IconSource;
using TabItem = System.Windows.Controls.TabItem;

namespace WebViewHub.Windows;

/// <summary>
/// Modal dialog for picking a service icon. Three tabs match the global
/// <see cref="IconSource"/> options. Default tab comes from Hub Settings.
/// </summary>
public partial class IconPickerWindow : FluentWindow
{
    private readonly string _serviceName;
    private readonly string _serviceUrl;
    private readonly HttpClient _http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // Some favicon CDNs (e.g. some direct site /favicon.ico requests)
        // reject the default .NET UA — set a real browser-like one.
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebViewHub/1.0");
        return c;
    }
    private List<HitCardVm> _cards = new();
    private HitCardVm? _selected;

    private IconSource _currentSource;
    private string? _singlePreviewUrl;
    private ImageSource? _singlePreviewImage;
    private bool _isInitialising = true;

    public MacOSIconsHit? SelectedHit { get; private set; }
    public ImageSource? SelectedPreview { get; private set; }

    public IconPickerWindow(string serviceName, string serviceUrl)
    {
        _serviceName = serviceName;
        _serviceUrl = serviceUrl ?? "";

        InitializeComponent();
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        TitleBarCtrl.Title = $"Choose icon for {serviceName}";

        MacIconsQueryBox.Text = serviceName;
        WebCatalogQueryBox.Text = serviceName;

        var defaultSource = SafeGetIconSource();
        SourceTabs.SelectedIndex = defaultSource switch
        {
            IconSource.Standard   => 0,
            IconSource.WebCatalog => 1,
            _                     => 2,
        };
        _currentSource = defaultSource;
        _isInitialising = false;

        Loaded += async (_, _) => await RunCurrentTabFetchAsync(forceRefresh: false);
    }

    private static IconSource SafeGetIconSource()
    {
        try { return App.Config?.Config?.HubSettings?.IconSource ?? IconSource.MacOSIcons; }
        catch { return IconSource.MacOSIcons; }
    }

    private async void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising) return;
        if (e.OriginalSource != SourceTabs) return; // ignore bubbled selection from inner controls

        var sel = SourceTabs.SelectedItem as TabItem;
        IconSource next;
        if      (sel == TabStandard)   next = IconSource.Standard;
        else if (sel == TabWebCatalog) next = IconSource.WebCatalog;
        else                           next = IconSource.MacOSIcons;
        if (next == _currentSource) return;

        _currentSource = next;
        ResetSelection();
        await RunCurrentTabFetchAsync(forceRefresh: false);
    }

    private void ResetSelection()
    {
        if (_selected != null) _selected.IsSelected = false;
        _selected = null;
        _singlePreviewUrl = null;
        _singlePreviewImage = null;
        StandardPreviewCard.Visibility = Visibility.Collapsed;
        WebCatalogPreviewCard.Visibility = Visibility.Collapsed;
        ApplyButton.IsEnabled = false;
    }

    private async Task RunCurrentTabFetchAsync(bool forceRefresh)
    {
        switch (_currentSource)
        {
            case IconSource.MacOSIcons:
                await SearchMacOSIconsAsync(MacIconsQueryBox.Text, forceRefresh);
                break;
            case IconSource.WebCatalog:
                await FetchWebCatalogPreviewAsync(WebCatalogQueryBox.Text);
                break;
            case IconSource.Standard:
                await FetchStandardFaviconAsync();
                break;
        }
    }

    // ─── Standard favicon ──────────────────────────────────────────────────

    private async Task FetchStandardFaviconAsync()
    {
        StandardPreviewCard.Visibility = Visibility.Collapsed;
        var host = TryExtractHost(_serviceUrl);
        if (string.IsNullOrEmpty(host))
        {
            StandardStatusText.Text = "Standard tab needs a valid service URL — open service settings, set the URL, then re-open the picker.";
            return;
        }

        StandardStatusText.Text = $"Fetching favicon for {host}…";

        // Fallback chain — same as FaviconService.GetIconAsync's favicon
        // tail (Google s2 → DuckDuckGo → direct /favicon.ico). Each source
        // fails for different subsets of domains; the chain catches the
        // common edge cases (Google 404s for some reserved domains, DDG
        // 404s for fresh domains, direct works only when the site itself
        // serves a real .ico). First successful download wins.
        var candidates = new (string url, string label)[]
        {
            ($"https://www.google.com/s2/favicons?domain={host}&sz=128",  $"Google favicon · {host}"),
            ($"https://icons.duckduckgo.com/ip3/{host}.ico",              $"DuckDuckGo favicon · {host}"),
            ($"https://{host}/favicon.ico",                               $"Site /favicon.ico · {host}"),
        };

        foreach (var (url, label) in candidates)
        {
            var bytes = await TryFetchAsync(url);
            if (bytes == null) continue;
            // Accept only real image bytes (PNG / ICO / GIF / JPEG / WebP)
            // — sites that 200-OK with HTML on unknown paths slip past the
            // status check otherwise.
            if (!LooksLikeImage(bytes)) continue;

            // ICO/WebP can't always go straight into BitmapImage; route
            // through WIC if needed.
            byte[] decoded = bytes;
            if (WebViewHub.Helpers.IconHelper.IsWebP(bytes) || LooksLikeIco(bytes))
            {
                decoded = WebViewHub.Helpers.IconHelper.ConvertImageToPngBytes(bytes) ?? bytes;
            }

            StandardStatusText.Text = "";
            ShowStandardPreview(_serviceName, label, url, decoded);
            return;
        }

        StandardStatusText.Text = $"No favicon found for {host} (tried Google, DuckDuckGo, and direct /favicon.ico).";
    }

    private static bool LooksLikeImage(byte[] b)
    {
        if (b == null || b.Length < 4) return false;
        // PNG
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
        // ICO (00 00 01 00) / CUR (00 00 02 00)
        if (b[0] == 0x00 && b[1] == 0x00 && (b[2] == 0x01 || b[2] == 0x02) && b[3] == 0x00) return true;
        // JPEG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
        // GIF
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return true;
        // WebP (RIFF????WEBP)
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return true;
        return false;
    }

    private static bool LooksLikeIco(byte[] b)
        => b != null && b.Length >= 4
           && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00;

    private static string? TryExtractHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Host;
        return null;
    }

    private void ShowStandardPreview(string name, string sourceLabel, string url, byte[] pngBytes)
    {
        if (!TryDecodeBitmap(pngBytes, out var bmp)) return;
        StandardPreviewImage.Source = bmp;
        StandardPreviewName.Text = name;
        StandardPreviewSource.Text = sourceLabel;
        StandardPreviewCard.Visibility = Visibility.Visible;
        _singlePreviewUrl = url;
        _singlePreviewImage = bmp;
        ApplyButton.IsEnabled = true;
    }

    // ─── WebCatalog single-preview ─────────────────────────────────────────

    private async Task FetchWebCatalogPreviewAsync(string query)
    {
        WebCatalogPreviewCard.Visibility = Visibility.Collapsed;
        var slug = SlugifyForWebCatalog((query ?? "").Trim());
        if (string.IsNullOrEmpty(slug))
        {
            WebCatalogStatusText.Text = "Type an app name — it will be slugified into the WebCatalog URL.";
            return;
        }

        var url = $"https://cdn-1.webcatalog.io/catalog/{slug}/{slug}-icon-filled-256.webp";
        WebCatalogStatusText.Text = $"Looking up '{slug}' on webcatalog.io…";
        var bytes = await TryFetchAsync(url);
        if (bytes == null)
        {
            WebCatalogStatusText.Text = $"No webcatalog.io icon for slug '{slug}'. Try a different name, or switch tabs.";
            return;
        }

        var pngBytes = WebViewHub.Helpers.IconHelper.ConvertImageToPngBytes(bytes) ?? bytes;
        WebCatalogStatusText.Text = "";
        ShowWebCatalogPreview(_serviceName, $"webcatalog.io · {slug}", url, pngBytes);
    }

    private static string SlugifyForWebCatalog(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var lower = name.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
    }

    private void ShowWebCatalogPreview(string name, string sourceLabel, string url, byte[] pngBytes)
    {
        if (!TryDecodeBitmap(pngBytes, out var bmp)) return;
        WebCatalogPreviewImage.Source = bmp;
        WebCatalogPreviewName.Text = name;
        WebCatalogPreviewSource.Text = sourceLabel;
        WebCatalogPreviewCard.Visibility = Visibility.Visible;
        _singlePreviewUrl = url;
        _singlePreviewImage = bmp;
        ApplyButton.IsEnabled = true;
    }

    private bool TryDecodeBitmap(byte[] pngBytes, out BitmapImage result)
    {
        try
        {
            using var ms = new MemoryStream(pngBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            result = bmp;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"IconPicker preview decode failed: {ex.Message}");
            result = new BitmapImage();
            return false;
        }
    }

    private void SinglePreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ApplyButton.IsEnabled) Apply_Click(sender, e);
    }

    // ─── macOSicons search ─────────────────────────────────────────────────

    private async Task SearchMacOSIconsAsync(string query, bool forceRefresh)
    {
        query = (query ?? "").Trim();
        if (string.IsNullOrEmpty(query))
        {
            ShowMacIconsStatus("Type an app name and press Search.");
            HideCacheStatus();
            return;
        }

        ShowMacIconsStatus(forceRefresh ? $"Refreshing '{query}'…" : $"Searching for '{query}'…");
        HideCacheStatus();
        ResultsList.ItemsSource = null;
        _cards.Clear();
        _selected = null;
        ApplyButton.IsEnabled = false;

        var (hits, cachedAtUtc, error) = await App.Favicon.SearchMacOSIconsAsync(query, hitsPerPage: 24, forceRefresh: forceRefresh);
        if (!string.IsNullOrEmpty(error))
        {
            ShowMacIconsStatus(error);
            return;
        }
        if (hits.Count == 0)
        {
            ShowMacIconsStatus($"No results for '{query}'.");
            ShowCacheStatus(0, cachedAtUtc);
            return;
        }

        HideMacIconsStatus();
        ShowCacheStatus(hits.Count, cachedAtUtc);
        _cards = hits.Select(h => new HitCardVm(h)).ToList();
        ResultsList.ItemsSource = _cards;
        foreach (var card in _cards) _ = LoadThumbnailAsync(card);
    }

    private async Task LoadThumbnailAsync(HitCardVm card)
    {
        var bytes = await TryFetchAsync(card.Hit.LowResPngUrl)
                    ?? await TryFetchAsync(card.Hit.IOSUrl);
        if (bytes == null) return;
        bytes = WebViewHub.Helpers.IconHelper.RoundPngCorners(bytes);
        Dispatcher.Invoke(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                card.Thumbnail = bmp;
            }
            catch (Exception ex)
            {
                Logger.Warn($"IconPicker thumb decode failed: {ex.Message}");
            }
        });
    }

    // ─── Shared search / refresh handlers ──────────────────────────────────

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        await RunCurrentTabFetchAsync(forceRefresh: false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RunCurrentTabFetchAsync(forceRefresh: true);
    }

    private async void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunCurrentTabFetchAsync(forceRefresh: false);
        }
    }

    private async Task<byte[]?> TryFetchAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (url.Contains("macosicons", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Referrer = new Uri("https://macosicons.com/");
            }
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Debug($"IconPicker fetch {(int)resp.StatusCode} for {url}");
                return null;
            }
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            Logger.Debug($"IconPicker fetch failed for {url}: {ex.Message}");
            return null;
        }
    }

    private void ResultsScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.4);
            e.Handled = true;
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is HitCardVm vm)
        {
            if (_selected != null) _selected.IsSelected = false;
            vm.IsSelected = true;
            _selected = vm;
            ApplyButton.IsEnabled = true;
        }
    }

    // ─── Apply / Cancel ────────────────────────────────────────────────────

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSource == IconSource.MacOSIcons)
        {
            if (_selected == null) return;
            var hit = _selected.Hit;
            var pngUrl = !string.IsNullOrEmpty(hit.IOSUrl) ? hit.IOSUrl : hit.LowResPngUrl;
            if (string.IsNullOrEmpty(pngUrl))
            {
                ShowMacIconsStatus("Selected hit has no PNG URL — try another.");
                return;
            }
            SelectedHit = hit;
            SelectedPreview = _selected.Thumbnail;
            Logger.Info($"[IconPicker] staged macOSicons '{hit.AppName}' (downloads={hit.Downloads}) for service '{_serviceName}'");
        }
        else
        {
            if (string.IsNullOrEmpty(_singlePreviewUrl)) return;
            SelectedHit = new MacOSIconsHit { AppName = _serviceName, IOSUrl = _singlePreviewUrl };
            SelectedPreview = _singlePreviewImage;
            Logger.Info($"[IconPicker] staged {_currentSource} '{_serviceName}' ← {_singlePreviewUrl}");
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ─── Status helpers ────────────────────────────────────────────────────

    private void ShowMacIconsStatus(string text)
    {
        MacIconsStatusText.Text = text;
        MacIconsStatusText.Visibility = Visibility.Visible;
    }

    private void HideMacIconsStatus() => MacIconsStatusText.Visibility = Visibility.Collapsed;

    private void ShowCacheStatus(int hitCount, DateTime? cachedAtUtc)
    {
        if (cachedAtUtc == null) { HideCacheStatus(); return; }
        var age = DateTime.UtcNow - cachedAtUtc.Value;
        string ago = age.TotalMinutes < 1 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
            : age.TotalDays < 7 ? $"{(int)age.TotalDays} d ago"
            : cachedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd");
        CacheStatusText.Text = $"{hitCount} hit{(hitCount == 1 ? "" : "s")} · cached {ago} (Refresh to re-fetch)";
        CacheStatusText.Visibility = Visibility.Visible;
    }

    private void HideCacheStatus() => CacheStatusText.Visibility = Visibility.Collapsed;

    private sealed class HitCardVm : INotifyPropertyChanged
    {
        public MacOSIconsHit Hit { get; }

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; Notify(); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; Notify(); Notify(nameof(BorderBrush)); }
        }

        public Brush BorderBrush => _isSelected
            ? (Brush)System.Windows.Application.Current.Resources["SystemAccentColorPrimaryBrush"]
              ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4))
            : (Brush)System.Windows.Application.Current.Resources["CardStrokeColorDefaultBrush"]
              ?? Brushes.Transparent;

        public string DownloadsText => $"{Hit.Downloads:N0} ↓";

        public string TooltipText =>
            string.IsNullOrEmpty(Hit.Credit) ? Hit.AppName ?? "" : $"{Hit.AppName}\nby {Hit.Credit}";

        public HitCardVm(MacOSIconsHit hit) { Hit = hit; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
