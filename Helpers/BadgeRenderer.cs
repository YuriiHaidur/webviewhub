using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace WebViewHub.Helpers;

/// <summary>
/// Composes a base icon with a red unread-count badge in the lower-right
/// corner — same look as Telegram / Slack tray icons.
/// </summary>
public static class BadgeRenderer
{
    /// <summary>
    /// Renders the base icon with a badge overlay. If count == 0, returns
    /// a copy of the base unchanged.
    /// </summary>
    public static Bitmap Render(Image baseImage, int count, int size = 64)
    {
        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(output);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(baseImage, 0, 0, size, size);

        if (count > 0)
        {
            DrawBadge(g, size, count);
        }

        return output;
    }

    /// <summary>
    /// Renders ONLY the badge circle on a transparent canvas — useful as
    /// TaskbarItemInfo.Overlay where Windows draws it on top of the
    /// taskbar icon natively.
    /// </summary>
    public static Bitmap RenderOverlay(int count, int size = 32)
    {
        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        if (count <= 0) return output;

        using var g = Graphics.FromImage(output);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Overlay icons fill the whole area Windows reserves — draw badge
        // at full size, not as a corner piece.
        var rect = new Rectangle(0, 0, size, size);
        var text = count > 99 ? "99+" : count.ToString();

        using var brush = new SolidBrush(Color.FromArgb(220, 38, 38));
        g.FillEllipse(brush, rect);
        using var pen = new Pen(Color.White, Math.Max(1f, size * 0.06f));
        g.DrawEllipse(pen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);

        DrawCenteredText(g, text, rect, Color.White);
        return output;
    }

    private static void DrawBadge(Graphics g, int canvas, int count)
    {
        var text = count > 99 ? "99+" : count.ToString();
        // Wider for multi-digit so digits don't get clipped.
        var badgeW = (int)(canvas * (text.Length switch
        {
            1 => 0.55,
            2 => 0.62,
            _ => 0.72
        }));
        var badgeH = (int)(canvas * 0.55);
        var x = canvas - badgeW;
        var y = canvas - badgeH;
        var rect = new Rectangle(x, y, badgeW, badgeH);

        using var brush = new SolidBrush(Color.FromArgb(220, 38, 38));
        if (text.Length == 1)
        {
            g.FillEllipse(brush, rect);
        }
        else
        {
            FillRoundedRect(g, rect, badgeH / 2, brush);
        }

        using var pen = new Pen(Color.White, Math.Max(1.5f, canvas * 0.045f));
        if (text.Length == 1) g.DrawEllipse(pen, rect);
        else DrawRoundedRect(g, rect, badgeH / 2, pen);

        DrawCenteredText(g, text, rect, Color.White);
    }

    private static void DrawCenteredText(Graphics g, string text, Rectangle rect, Color color)
    {
        // Pick a font size that fits the rect height with some padding.
        var fontSize = Math.Max(8f, rect.Height * 0.62f);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        // Slight upward nudge — Segoe UI sits a bit low in this box.
        var textRect = new RectangleF(rect.X, rect.Y - rect.Height * 0.05f, rect.Width, rect.Height);
        g.DrawString(text, font, brush, textRect, sf);
    }

    private static void FillRoundedRect(Graphics g, Rectangle r, int radius, Brush brush)
    {
        using var path = RoundedPath(r, radius);
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRect(Graphics g, Rectangle r, int radius, Pen pen)
    {
        using var path = RoundedPath(r, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Bitmap → BitmapImage (frozen, safe to assign to Window.Icon).
    /// </summary>
    public static BitmapImage ToBitmapImage(Bitmap bmp)
    {
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
}
