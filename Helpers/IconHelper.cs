using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfDrawingVisual = System.Windows.Media.DrawingVisual;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfFontStretches = System.Windows.FontStretches;
using WpfFontStyles = System.Windows.FontStyles;
using WpfFontWeights = System.Windows.FontWeights;
using WpfFormattedText = System.Windows.Media.FormattedText;
using WpfPixelFormats = System.Windows.Media.PixelFormats;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfTypeface = System.Windows.Media.Typeface;

namespace WebViewHub.Helpers;

/// <summary>
/// Loads icons in formats usable by both WPF (Window.Icon as ImageSource)
/// and WinForms NotifyIcon (System.Drawing.Icon).
/// </summary>
public static class IconHelper
{
    /// <summary>
    /// Loads a WPF ImageSource from a PNG/ICO file path. Returns null on failure.
    ///
    /// Bytes are read into a MemoryStream first instead of using
    /// <see cref="BitmapImage.UriSource"/>. WPF aggressively caches
    /// BitmapImage by URI — reusing the same path returns the previous
    /// image even when the file on disk has changed (the icon-picker
    /// replace case). StreamSource bypasses that cache so every call
    /// reads fresh bytes.
    /// </summary>
    /// <summary>
    /// Loads a PNG/ICO from disk and re-renders it at <paramref name="targetSize"/>
    /// via WPF's <see cref="RenderTargetBitmap"/>. Gives gamma-correct
    /// downscale from a high-res source (1024px macOSicons PNGs) to a
    /// taskbar-friendly intermediate size (256px). Windows then scales
    /// that to 16/32/48 for HICON-based slots with much less aliasing
    /// than scaling directly from 1024.
    ///
    /// Use this specifically for <see cref="System.Windows.Window.Icon"/>
    /// so the taskbar gets a crisp source. The on-disk file is unchanged.
    /// </summary>
    public static BitmapSource? LoadWpfImageScaled(string? path, int targetSize)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var src = new BitmapImage();
            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.StreamSource = ms;
            src.EndInit();
            src.Freeze();

            // Never upscale — when source < targetSize (e.g. cache file
            // came from the 128px lowResPngUrl fallback), upscaling here
            // would just add blur that Windows then downscales again. Use
            // the source's native size and let WPF/Windows downscale once.
            var renderSize = Math.Min(targetSize, Math.Max(src.PixelWidth, src.PixelHeight));
            if (renderSize <= 0) renderSize = targetSize;

            var rtb = new RenderTargetBitmap(renderSize, renderSize, 96, 96, WpfPixelFormats.Pbgra32);
            var visual = new WpfDrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(src, new WpfRect(0, 0, renderSize, renderSize));
            }
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"LoadWpfImageScaled('{path}', {targetSize}) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Derives a soft pastel vertical gradient from the dominant colour
    /// of the icon, suitable for the Hub-tile gradient header (Microsoft
    /// "Windows App" style — each tile takes its accent from the actual
    /// app icon). Samples opaque interior pixels, averages RGB, then
    /// blends toward white for both stops so the result stays light
    /// enough to read foreground text against either theme. Returns a
    /// neutral gray gradient on any failure.
    /// </summary>
    public static System.Windows.Media.Brush BuildIconGradient(string? iconPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                using var bmp = new Bitmap(iconPath);
                var dominant = ExtractDominantOpaqueColor(bmp);
                return CreatePastelGradient(dominant);
            }
        }
        catch (Exception ex)
        {
            Services.Logger.Debug($"BuildIconGradient('{iconPath}') failed: {ex.Message}");
        }
        // Neutral fallback — pale gray gradient.
        return CreatePastelGradient(System.Windows.Media.Color.FromRgb(200, 200, 200));
    }

    private static System.Windows.Media.Color ExtractDominantOpaqueColor(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        // Sample the inner 50% of the icon — outer 25% is mostly the
        // squircle's transparent corner halo, which would bias the average
        // toward whatever lives near the curve.
        int minX = w / 4, maxX = w * 3 / 4;
        int minY = h / 4, maxY = h * 3 / 4;
        long r = 0, g = 0, b = 0;
        long count = 0;
        int step = Math.Max(1, w / 32);
        for (int y = minY; y < maxY; y += step)
        {
            for (int x = minX; x < maxX; x += step)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A < 200) continue;
                r += c.R; g += c.G; b += c.B; count++;
            }
        }
        if (count == 0) return System.Windows.Media.Color.FromRgb(200, 200, 200);
        return System.Windows.Media.Color.FromRgb(
            (byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    private static System.Windows.Media.Brush CreatePastelGradient(
        System.Windows.Media.Color baseColor)
    {
        // Flat single-colour fill — was a vertical gradient earlier but
        // the design now matches the WinUI Gallery "tinted card" pattern:
        // one solid pastel per tile, no top/bottom stops. The bottom-plate
        // overlay (controlled by the Hub header slider) still darkens the
        // lower half for legibility — that's an independent layer.
        //
        // Theme-aware tint: in Light mode blend toward white for a pastel;
        // in Dark mode blend slightly toward black so the brand chroma
        // stays visible against the dark Mica backdrop. Tint sits roughly
        // between the previous gradient's two stops.
        bool isDark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var tint = isDark
            ? Lerp(baseColor, System.Windows.Media.Colors.Black, 0.25)
            : Lerp(baseColor, System.Windows.Media.Colors.White, 0.65);
        double opacity = isDark ? 0.50 : 0.55;

        var brush = new System.Windows.Media.SolidColorBrush(tint) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Color Lerp(
        System.Windows.Media.Color a, System.Windows.Media.Color b, double t)
    {
        return System.Windows.Media.Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>
    /// True if the byte stream starts with the WebP RIFF signature
    /// (<c>"RIFF????WEBP"</c>). Win11 has a built-in webp codec but
    /// not every Win10 install does — detect explicitly so we can
    /// route through WPF's BitmapDecoder (WIC) before feeding
    /// <see cref="NormalizeIcon"/>'s GDI+ pipeline.
    /// </summary>
    public static bool IsWebP(byte[] bytes)
    {
        return bytes != null && bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46  // "RIFF"
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50; // "WEBP"
    }

    /// <summary>
    /// Decodes any WIC-supported image format (webp, avif, heif, png,
    /// jpg, gif, bmp, …) and re-encodes the first frame as a PNG byte
    /// array. Used to normalize <c>image/webp</c> downloads from
    /// webcatalog.io into a format <see cref="NormalizeIcon"/>'s GDI+
    /// pipeline can reliably consume regardless of which OS codecs
    /// are installed.
    /// </summary>
    public static byte[]? ConvertImageToPngBytes(byte[] inputBytes)
    {
        if (inputBytes == null || inputBytes.Length < 12) return null;
        try
        {
            using var ms = new MemoryStream(inputBytes);
            // PreservePixelFormat keeps the alpha channel — default
            // would convert to BGR32 and drop transparency.
            var decoder = BitmapDecoder.Create(ms,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(decoder.Frames[0]);

            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"ConvertImageToPngBytes failed: {ex.Message}");
            return null;
        }
    }

    public static BitmapImage? LoadWpfImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try
        {
            // StreamSource (over a MemoryStream of the just-read bytes)
            // bypasses WPF's URI-keyed cache on its own — UriSource would
            // return the previously cached BitmapImage even after the
            // file on disk had been overwritten by the icon picker.
            // BitmapCacheOption.OnLoad reads the entire stream into the
            // BitmapImage's internal cache, so the MemoryStream can be
            // disposed right after EndInit.
            //
            // Don't add BitmapCreateOptions.IgnoreImageCache here: that
            // flag is only meaningful for UriSource and crashes with
            // "Value cannot be null. (Parameter 'key')" when combined
            // with StreamSource — the internal cache lookup needs a URI
            // key that doesn't exist for stream-loaded images.
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"LoadWpfImage('{path}') failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads a System.Drawing.Icon from a PNG/ICO file. Returns null on failure.
    /// </summary>
    public static Icon? LoadIcon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try
        {
            using var bmp = new Bitmap(path);
            return BitmapToIcon(bmp);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates a simple square icon with the first letter of the name.
    /// Used when favicon download fails or while it's still in progress.
    /// </summary>
    public static Icon GenerateLetterIcon(string name, int size = 64)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // Background — squircle (iOS-style rounded square) so the
            // fallback matches the shape of macOSicons-fetched icons.
            var color = ColorFromString(name);
            var radius = Math.Max(2, (int)Math.Round(size * 0.22));
            using (var brush = new SolidBrush(color))
            using (var path = RoundedRectPath(0, 0, size, size, radius))
            {
                g.FillPath(brush, path);
            }

            // Letter
            var letter = (string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1]).ToUpper();
            using var font = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(letter, font, textBrush, new RectangleF(0, 0, size, size), sf);
        }

        return BitmapToIcon(bmp);
    }

    /// <summary>
    /// Same as GenerateLetterIcon but returns a WPF ImageSource.
    /// </summary>
    public static BitmapImage GenerateLetterImage(string name, int size = 64)
    {
        using var icon = GenerateLetterIcon(name, size);
        using var bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>
    /// Standard ICO frame sizes packed by <see cref="BuildMultiFrameIcoBytes"/>.
    /// Covers tray (16/20/24/32) and taskbar/alt-tab (40/48/64/256) across
    /// 100/125/150/200% DPI scales so Windows can pick an exact frame
    /// instead of bicubic-shrinking a single 256px source.
    /// </summary>
    private static readonly int[] IcoFrameSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    /// <summary>
    /// Converts an arbitrary Bitmap into a System.Drawing.Icon suitable
    /// for NotifyIcon. Public so callers can wrap the badge-rendered
    /// bitmap from BadgeRenderer.
    ///
    /// <para>
    /// When <paramref name="traySize"/> is supplied (DPI-aware tray slot
    /// size from <see cref="NativeMethods.GetTrayIconSize"/>), produces a
    /// <b>single-frame HICON sized exactly for the tray slot</b>. This
    /// bypasses NotifyIcon's known sizing bug (dotnet/winforms#6955) and
    /// Windows' naive nearest-neighbour tray downscale — every pixel of
    /// the source bitmap maps 1:1 onto a tray pixel.
    /// </para>
    /// <para>
    /// When <paramref name="traySize"/> is null, builds a true multi-frame
    /// ICO (8 sizes) so Windows can pick the best frame for callers that
    /// don't know the target slot ahead of time (e.g. HubWindow's tray
    /// icon shared across DPI changes).
    /// </para>
    /// </summary>
    public static Icon BitmapToIcon(Bitmap bmp, int? traySize = null)
    {
        if (traySize.HasValue && traySize.Value > 0)
        {
            // Sized-fit path. If the bitmap is already at the target
            // size, skip the resize entirely.
            bool needsResize = bmp.Width != traySize.Value || bmp.Height != traySize.Value;
            Bitmap sized = needsResize ? HighQualityResize(bmp, traySize.Value, traySize.Value) : bmp;
            Services.Logger.Debug($"[IconDbg] BitmapToIcon sized-fit path: src={bmp.Width}x{bmp.Height} → target={traySize.Value}x{traySize.Value} resize={needsResize}");
            try
            {
                var hIcon = sized.GetHicon();
                try
                {
                    var icon = (Icon)Icon.FromHandle(hIcon).Clone();
                    Services.Logger.Debug($"[IconDbg] BitmapToIcon → Icon.Width={icon.Width} Icon.Height={icon.Height}");
                    return icon;
                }
                finally { DestroyIcon(hIcon); }
            }
            finally
            {
                if (!ReferenceEquals(sized, bmp)) sized.Dispose();
            }
        }
        Services.Logger.Debug($"[IconDbg] BitmapToIcon multi-frame path: src={bmp.Width}x{bmp.Height} (no traySize hint)");

        try
        {
            var icoBytes = BuildMultiFrameIcoBytes(bmp, IcoFrameSizes);
            using var ms = new MemoryStream(icoBytes);
            return new Icon(ms);
        }
        catch
        {
            var iconSize = bmp.Width > 256 ? 256 : (bmp.Width < 16 ? 16 : bmp.Width);
            using var resized = HighQualityResize(bmp, iconSize, iconSize);
            var hIcon = resized.GetHicon();
            try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
            finally { DestroyIcon(hIcon); }
        }
    }

    /// <summary>
    /// Packs a multi-frame Windows ICO container with one PNG-compressed
    /// frame per <paramref name="sizes"/> entry. Each frame is produced
    /// from <paramref name="source"/> via <see cref="HighQualityResize"/>.
    ///
    /// ICO layout (Win10+ supports PNG payload — no need for the legacy
    /// DIB+AND-mask form):
    ///   ICONDIR (6 bytes)
    ///     uint16 reserved = 0
    ///     uint16 type     = 1 (icon)
    ///     uint16 count    = N
    ///   ICONDIRENTRY × N (16 bytes each)
    ///     uint8  width    (0 = 256)
    ///     uint8  height   (0 = 256)
    ///     uint8  colorCount = 0
    ///     uint8  reserved   = 0
    ///     uint16 planes     = 1
    ///     uint16 bpp        = 32
    ///     uint32 dataSize
    ///     uint32 dataOffset
    ///   payload × N (raw PNG bytes)
    /// </summary>
    public static byte[] BuildMultiFrameIcoBytes(Bitmap source, int[] sizes)
    {
        var pngs = new List<byte[]>(sizes.Length);
        foreach (var size in sizes)
        {
            using var resized = HighQualityResize(source, size, size);
            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }

        using var outStream = new MemoryStream();
        using var bw = new BinaryWriter(outStream);

        bw.Write((ushort)0);            // reserved
        bw.Write((ushort)1);            // type = icon
        bw.Write((ushort)pngs.Count);   // entry count

        int dataOffset = 6 + 16 * pngs.Count;
        for (int i = 0; i < pngs.Count; i++)
        {
            int size = sizes[i];
            byte dim = (byte)(size >= 256 ? 0 : size);
            bw.Write(dim);              // width
            bw.Write(dim);              // height
            bw.Write((byte)0);          // colorCount (0 for true-color)
            bw.Write((byte)0);          // reserved
            bw.Write((ushort)1);        // planes
            bw.Write((ushort)32);       // bits per pixel
            bw.Write((uint)pngs[i].Length); // payload size
            bw.Write((uint)dataOffset); // payload offset
            dataOffset += pngs[i].Length;
        }

        foreach (var png in pngs)
        {
            bw.Write(png);
        }

        bw.Flush();
        return outStream.ToArray();
    }

    /// <summary>
    /// Gamma-friendly downscale via GDI+ with HighQualityBicubic + AA.
    /// The naive <c>new Bitmap(src, size)</c> constructor uses GDI's
    /// default near-nearest-neighbor downsampler, which is what made our
    /// tray icons look "shakal" when shrunk from 1024px source PNGs.
    /// </summary>
    private static Bitmap HighQualityResize(Bitmap src, int width, int height)
    {
        var dst = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        // Wrap mode prevents bleed of edge pixels into transparent areas
        // when downsampling — important for the squircle alpha mask.
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
        g.DrawImage(src, new Rectangle(0, 0, width, height),
            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    /// <summary>
    /// Canonical icon normalization pipeline ported from rikumi/iconsur
    /// (MIT). One function that takes ANY source — full-bleed iOSUrl,
    /// macOS-rendered lowResPngUrl, .icns extracted frame, raw favicon —
    /// and produces a uniform 1024×1024 PNG:
    ///   1. Auto-crop the input to its alpha bounding box so source
    ///      padding (transparent rim around the artwork) doesn't add to
    ///      ours.
    ///   2. Aspect-fit into <paramref name="scaleRatio"/> of the canvas,
    ///      centered. Default 1.0 = edge-to-edge fill (matches the
    ///      "old Slack-style" baseline where the icon visually fills its
    ///      Hub tile and tray slot). Drop to ~0.9 for macOS-Dock
    ///      breathing room; values > 1.0 overflow and get clipped by
    ///      the squircle mask.
    ///   3. Mask through a pre-rendered macOS-accurate squircle PNG
    ///      shipped with the app (Resources/SquircleMask.png). This gives
    ///      Apple's actual continuous-curvature shape — no math approx.
    ///   4. Encode and return.
    ///
    /// Every downstream consumer (Hub Image, NotifyIcon, .ico generator,
    /// Window.Icon → taskbar) reads the same canonical file, so the
    /// "icons look smaller than native Windows apps" / "Hub doesn't
    /// match picker preview" classes of bugs go away by construction.
    /// </summary>
    public static byte[] NormalizeIcon(byte[] inputBytes, double scaleRatio = 1.0)
    {
        if (inputBytes == null || inputBytes.Length < 16) return inputBytes ?? Array.Empty<byte>();

        try
        {
            using var inStream = new MemoryStream(inputBytes);
            using var src = (Bitmap)System.Drawing.Image.FromStream(inStream);

            // Step 1: trim transparent borders so every source contributes
            // the same "visible content rectangle".
            //
            // Two-pass adaptive threshold:
            //   • Pass 1 at alpha=8 catches normal "transparent margin"
            //     icons (most macOSicons iOSUrl assets, webcatalog webp).
            //   • If pass 1 returns ~full canvas the source likely has a
            //     macOS Dock drop shadow extending to the edges with
            //     alpha 10-30 (typical of .icns frame extractions). Retry
            //     at alpha=64 to skip the shadow halo and find the real
            //     visible-content rectangle. If THAT also covers ~full
            //     canvas the icon really is full-bleed (like Slack's
            //     dark-gray squircle) — keep the first bbox.
            var bbox = FindAlphaBoundingBox(src, alphaThreshold: 8);
            if (bbox.IsEmpty || bbox.Width <= 0 || bbox.Height <= 0) return inputBytes;
            const double FullCanvasPct = 0.95;
            bool firstPassFull = bbox.Width >= src.Width * FullCanvasPct
                              && bbox.Height >= src.Height * FullCanvasPct;
            if (firstPassFull)
            {
                var tightBbox = FindAlphaBoundingBox(src, alphaThreshold: 64);
                bool secondPassFull = tightBbox.Width >= src.Width * FullCanvasPct
                                   && tightBbox.Height >= src.Height * FullCanvasPct;
                if (!secondPassFull && !tightBbox.IsEmpty && tightBbox.Width > 0 && tightBbox.Height > 0)
                {
                    Services.Logger.Debug($"NormalizeIcon adaptive crop: drop-shadow detected. bbox8={bbox.Width}x{bbox.Height}, bbox64={tightBbox.Width}x{tightBbox.Height} — using tighter.");
                    bbox = tightBbox;
                }
            }
            using var cropped = src.Clone(bbox, PixelFormat.Format32bppArgb);

            const int canvasSize = 1024;
            var targetSize = (int)Math.Round(canvasSize * scaleRatio);

            // Step 2: aspect-fit the cropped content into an inner box,
            // centered on a transparent 1024 canvas.
            double srcAspect = (double)cropped.Width / cropped.Height;
            int drawW, drawH;
            if (srcAspect >= 1)
            {
                drawW = targetSize;
                drawH = (int)Math.Round(targetSize / srcAspect);
            }
            else
            {
                drawH = targetSize;
                drawW = (int)Math.Round(targetSize * srcAspect);
            }
            int drawX = (canvasSize - drawW) / 2;
            int drawY = (canvasSize - drawH) / 2;

            using var canvas = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(cropped, new Rectangle(drawX, drawY, drawW, drawH),
                    0, 0, cropped.Width, cropped.Height, GraphicsUnit.Pixel, attrs);
            }

            // Step 3: pixel-wise alpha-mask through the macOS squircle PNG
            // shipped as a resource. Mask is 1024×1024 already so no resize
            // needed when canvasSize == 1024.
            ApplySquircleMask(canvas);

            using var outStream = new MemoryStream();
            canvas.Save(outStream, ImageFormat.Png);
            return outStream.ToArray();
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"NormalizeIcon failed, falling back to raw input: {ex.Message}");
            return inputBytes;
        }
    }

    private static Bitmap? _cachedSquircleMask;
    private static readonly object _maskLock = new();

    /// <summary>
    /// Loads the bundled macOS squircle PNG once and caches it. Resource
    /// is a 1024×1024 RGBA PNG; alpha channel is the shape. We only need
    /// the alpha, so we apply <c>output.a = min(input.a, mask.a)</c> per
    /// pixel — same effect as iconsur's bitwise AND but more explicit.
    /// </summary>
    private static Bitmap GetSquircleMask()
    {
        lock (_maskLock)
        {
            if (_cachedSquircleMask != null) return _cachedSquircleMask;
            var uri = new Uri("pack://application:,,,/Resources/SquircleMask.png");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream
                ?? throw new InvalidOperationException("SquircleMask.png resource missing");
            using var raw = (Bitmap)System.Drawing.Image.FromStream(stream);

            // Bundled SquircleMask.png from rikumi/iconsur has ~9% built-in
            // canvas padding (the shape itself occupies ~841×841 of a
            // 1024×1024 canvas). Applying it as-is shrinks every normalized
            // icon to 82% of canvas. Crop to the mask's visible bbox and
            // rescale to canvas so the squircle FILLS the canvas — content
            // mapped through the mask then fills 100% of the slot.
            var bbox = FindAlphaBoundingBox(raw, alphaThreshold: 8);
            if (bbox.IsEmpty || bbox.Width <= 0 || bbox.Height <= 0)
            {
                _cachedSquircleMask = new Bitmap(raw);
                return _cachedSquircleMask;
            }

            // Crop to bbox, then scale back to original canvas size.
            using var cropped = raw.Clone(bbox, PixelFormat.Format32bppArgb);
            var rescaled = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(rescaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(cropped, new Rectangle(0, 0, raw.Width, raw.Height),
                    0, 0, cropped.Width, cropped.Height, GraphicsUnit.Pixel, attrs);
            }
            _cachedSquircleMask = rescaled;
            Services.Logger.Debug($"GetSquircleMask: rescaled {bbox.Width}x{bbox.Height} → {raw.Width}x{raw.Height} (filling canvas).");
            return _cachedSquircleMask;
        }
    }

    /// <summary>
    /// Multiplies the canvas alpha channel by the squircle mask alpha.
    /// LockBits + unsafe pointer walk for speed (1M+ pixels per call;
    /// GetPixel/SetPixel would be ~100× slower).
    /// </summary>
    private static unsafe void ApplySquircleMask(Bitmap canvas)
    {
        var mask = GetSquircleMask();
        if (mask.Width != canvas.Width || mask.Height != canvas.Height)
        {
            // Resize mask once and cache the resized copy. For our use
            // canvas is always 1024 so this branch shouldn't run, but
            // keep it for future flexibility.
            using var resized = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(mask, 0, 0, canvas.Width, canvas.Height);
            }
            ApplyMaskBits(canvas, resized);
            return;
        }
        ApplyMaskBits(canvas, mask);
    }

    private static unsafe void ApplyMaskBits(Bitmap canvas, Bitmap mask)
    {
        var rect = new Rectangle(0, 0, canvas.Width, canvas.Height);
        var cd = canvas.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var md = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < canvas.Height; y++)
            {
                byte* cRow = (byte*)cd.Scan0 + y * cd.Stride;
                byte* mRow = (byte*)md.Scan0 + y * md.Stride;
                for (int x = 0; x < canvas.Width; x++)
                {
                    // BGRA layout — alpha is byte 3.
                    int idx = x * 4 + 3;
                    int newAlpha = (cRow[idx] * mRow[idx]) / 255;
                    cRow[idx] = (byte)newAlpha;
                }
            }
        }
        finally
        {
            canvas.UnlockBits(cd);
            mask.UnlockBits(md);
        }
    }

    /// <summary>
    /// Scans the alpha channel and returns the tightest rectangle that
    /// contains every pixel with alpha &gt; <paramref name="alphaThreshold"/>.
    /// Returns Rectangle.Empty when the bitmap is fully transparent.
    /// </summary>
    private static unsafe Rectangle FindAlphaBoundingBox(Bitmap bmp, byte alphaThreshold = 8)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (row[x * 4 + 3] > alphaThreshold)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Legacy method kept as a fallback path for non-image inputs and as
    /// a no-op alias — every call site has been migrated to
    /// <see cref="NormalizeIcon"/> which does auto-crop + scale +
    /// canonical squircle mask in one shot.</summary>
    /// <remarks>
    /// Returns a copy of the input PNG bytes with corners clipped to an
    /// iOS-style squircle approximation (~22% radius, regular rounded
    /// rectangle — close enough to Apple's actual superellipse for the
    /// 16-256px sizes we ever render). The mask is baked into the alpha
    /// channel so every downstream consumer (WPF Image, NotifyIcon, .ico
    /// for shortcuts, Window.Icon for taskbar) sees the rounded shape
    /// without per-control clipping.
    ///
    /// No extra inset is applied: macOSicons hosts iOS marketing assets
    /// that are deliberately full-bleed because iOS itself draws the
    /// system squircle on top. Most artists also bake their preferred
    /// padding INTO the artwork (a black/colored squircle drawn inset
    /// inside the 1024×1024 canvas), so adding an extra inset here
    /// produced visible double-padding.
    ///
    /// Returns the original bytes unchanged if decoding fails — favicon
    /// downloads sometimes hand us junk and we'd rather keep a square
    /// icon than nothing.
    /// </summary>
    public static byte[] RoundPngCorners(byte[] inputBytes)
    {
        if (inputBytes == null || inputBytes.Length < 16) return inputBytes ?? Array.Empty<byte>();

        try
        {
            using var inStream = new MemoryStream(inputBytes);
            using var src = (Bitmap)System.Drawing.Image.FromStream(inStream);

            var w = src.Width;
            var h = src.Height;
            if (w <= 0 || h <= 0) return inputBytes;

            var minSide = Math.Min(w, h);
            var radius = (int)Math.Round(minSide * 0.22);
            if (radius < 2) radius = 2;
            if (radius * 2 > minSide) radius = minSide / 2;

            using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                using var path = RoundedRectPath(0, 0, w, h, radius);
                g.SetClip(path);
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }

            using var outStream = new MemoryStream();
            dst.Save(outStream, ImageFormat.Png);
            return outStream.ToArray();
        }
        catch
        {
            return inputBytes;
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(int x, int y, int w, int h, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Loads any image (PNG/ICO) from disk as a Bitmap at the requested
    /// size using high-quality bicubic downscale (matters for the
    /// 1024px → 64px reduction we do on every macOSicons-sourced icon).
    /// Returns a generated letter bitmap if the path is missing/unreadable.
    /// </summary>
    public static Bitmap LoadOrGenerateBitmap(string? path, string fallbackName, int size = 64)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                using var src = new Bitmap(path);
                return HighQualityResize(src, size, size);
            }
            catch { /* fall through */ }
        }
        return GenerateLetterBitmap(fallbackName, size);
    }

    /// <summary>
    /// Same as GenerateLetterIcon but returns a Bitmap so it can be used
    /// as base for the badge renderer.
    /// </summary>
    public static Bitmap GenerateLetterBitmap(string name, int size = 64)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        // Squircle background matches the iOS-style mask we bake into
        // macOSicons downloads, so the fallback shape is consistent.
        var color = ColorFromString(name);
        var radius = Math.Max(2, (int)Math.Round(size * 0.22));
        using (var brush = new SolidBrush(color))
        using (var path = RoundedRectPath(0, 0, size, size, radius))
        {
            g.FillPath(brush, path);
        }

        var letter = (string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1]).ToUpper();
        using var font = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(letter, font, textBrush, new RectangleF(0, 0, size, size), sf);
        return bmp;
    }

    /// <summary>
    /// Writes a PNG source file into a side-by-side multi-resolution .ico
    /// file (next to the source). Used for Start-menu shortcuts, where
    /// Windows expects a real .ico — and where, crucially, Explorer picks
    /// the right size for the current view (16 in list, 32 in medium, 256
    /// in extra-large) rather than scaling a single frame.
    ///
    /// Each frame is rendered via WPF (gamma-correct resize) and PNG-encoded
    /// inside the ICO. Skipping System.Drawing here is what keeps the
    /// brand colours intact — the previous Bitmap.GetHicon() path produced
    /// a single 64×64 frame with muddy edges that Explorer then nearest-
    /// neighbour'd down to 16, hence the "broken" look in the screenshot.
    /// </summary>
    public static string? EnsureIcoFile(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;

        // Already an .ico — use as-is.
        if (string.Equals(Path.GetExtension(sourcePath), ".ico", StringComparison.OrdinalIgnoreCase))
            return sourcePath;

        var icoPath = Path.ChangeExtension(sourcePath, ".ico");
        try
        {
            // FaviconService saves every download under a .png filename even
            // when the bytes are actually ICO (e.g. DDG fallback). If the
            // source is already a real ICO, just copy it — no need to
            // re-encode each frame.
            var hdr = new byte[4];
            bool isIco;
            using (var probe = File.OpenRead(sourcePath))
            {
                isIco = probe.Read(hdr, 0, 4) == 4 &&
                        hdr[0] == 0x00 && hdr[1] == 0x00 && hdr[2] == 0x01 && hdr[3] == 0x00;
            }
            if (isIco)
            {
                File.Copy(sourcePath, icoPath, overwrite: true);
                return icoPath;
            }
        }
        catch { /* fall through to the re-encode path */ }

        try
        {
            // Sentinel: a fresh multi-frame ICO with 8 PNG payloads is
            // always >= ~10 KB. Anything older or smaller is a stale
            // single-frame leftover — regenerate.
            if (File.Exists(icoPath))
            {
                var fresh = File.GetLastWriteTimeUtc(icoPath) >= File.GetLastWriteTimeUtc(sourcePath);
                var bigEnough = new FileInfo(icoPath).Length >= 10000;
                if (fresh && bigEnough) return icoPath;
            }

            // Delegate to the shared multi-frame builder (GDI+ pipeline —
            // thread-safe, callable from background tasks). Same generator
            // that BitmapToIcon uses for in-memory NotifyIcon updates, so
            // tray + taskbar + Start-menu shortcuts all share one frame
            // recipe.
            using var src = (Bitmap)System.Drawing.Image.FromFile(sourcePath);
            var icoBytes = BuildMultiFrameIcoBytes(src, IcoFrameSizes);
            File.WriteAllBytes(icoPath, icoBytes);

            return icoPath;
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"EnsureIcoFile('{sourcePath}') failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pack URI for the Fluent System Icons font shipped inside the WPF-UI
    /// assembly. We render Window.Icon glyphs from this so the hub gets a
    /// proper Fluent-style icon instead of a generated letter.
    /// </summary>
    private const string FluentRegularFontUri =
        "pack://application:,,,/Wpf.Ui;component/Resources/Fonts/#FluentSystemIcons-Regular";

    /// <summary>
    /// Renders a WPF-UI Fluent symbol as a square image suitable for
    /// Window.Icon. Background is a flat-coloured rounded square; the
    /// glyph is drawn centred in white. Identical visual recipe to a
    /// modern Windows app tile.
    /// </summary>
    public static BitmapSource GenerateSymbolImage(
        SymbolRegular symbol,
        WpfColor background,
        int size = 64)
    {
        var rtb = new RenderTargetBitmap(size, size, 96, 96, WpfPixelFormats.Pbgra32);
        var visual = new WpfDrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            // Rounded-square background.
            var bgBrush = new WpfSolidColorBrush(background);
            bgBrush.Freeze();
            var radius = size * 0.22; // ≈ Win11 squircle proportion
            dc.DrawRoundedRectangle(bgBrush, null, new WpfRect(0, 0, size, size), radius, radius);

            // Glyph — Fluent Icons font ships unicode-mapped where the enum
            // value IS the codepoint, so a single ConvertFromUtf32 is enough.
            var glyph = char.ConvertFromUtf32((int)symbol);
            var fontFamily = new WpfFontFamily(new Uri("pack://application:,,,/"), FluentRegularFontUri);
            var typeface = new WpfTypeface(fontFamily, WpfFontStyles.Normal, WpfFontWeights.Regular, WpfFontStretches.Normal);

            var ft = new WpfFormattedText(
                glyph,
                CultureInfo.InvariantCulture,
                WpfFlowDirection.LeftToRight,
                typeface,
                size * 0.6,
                WpfBrushes.White,
                pixelsPerDip: 1.0);

            var x = (size - ft.Width) / 2.0;
            var y = (size - ft.Height) / 2.0;
            dc.DrawText(ft, new WpfPoint(x, y));
        }

        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Color ColorFromString(string s)
    {
        if (string.IsNullOrEmpty(s)) return Color.Gray;

        // Stable colour per-name from a hash.
        unchecked
        {
            int hash = 17;
            foreach (var c in s) hash = hash * 31 + c;

            // Pick a pleasant HSL: fixed S/L, hue from hash.
            double hue = (Math.Abs(hash) % 360);
            return ColorFromHsl(hue, 0.55, 0.45);
        }
    }

    private static Color ColorFromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = h / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;

        if (hp < 1) { r = c; g = x; }
        else if (hp < 2) { r = x; g = c; }
        else if (hp < 3) { g = c; b = x; }
        else if (hp < 4) { g = x; b = c; }
        else if (hp < 5) { r = x; b = c; }
        else { r = c; b = x; }

        double m = l - c / 2;
        return Color.FromArgb(
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }
}
