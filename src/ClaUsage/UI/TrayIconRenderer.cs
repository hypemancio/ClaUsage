using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaUsage.Core;

namespace ClaUsage.UI;

/// <summary>Draws the tray icon: the highest usage as a number on a colored tile, or a ring gauge.</summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    private static IntPtr _lastHandle;

    public static int IconSize
    {
        get
        {
            try { return Math.Max(16, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem())); }
            catch { return 16; }
        }
    }

    public static Icon Render(double? fraction, AppSettings settings, bool stale)
    {
        var size = IconSize;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (fraction is { } f && settings.TrayDisplay == TrayDisplay.HighestPercent)
                DrawNumber(g, size, f, settings, stale);
            else
                DrawRing(g, size, fraction, settings, stale);
        }

        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        // The clone owns a copy; free the previous native handle so the tray doesn't leak GDI objects.
        if (_lastHandle != IntPtr.Zero) DestroyIcon(_lastHandle);
        _lastHandle = handle;
        return icon;
    }

    /// <summary>The app's own icon at <paramref name="size"/>, for windows and the .ico file.</summary>
    public static Bitmap RenderAppBitmap(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        DrawRing(g, size, 0.68, new AppSettings(), stale: false, appIcon: true);
        return bitmap;
    }

    private static Color Severity(double fraction, AppSettings settings, bool stale)
    {
        if (stale) return Color.FromArgb(0x8E, 0x8E, 0x93);
        var percent = fraction * 100;
        if (percent >= settings.CriticalPercent) return Color.FromArgb(0xE0, 0x38, 0x2F);
        if (percent >= settings.WarnPercent) return Color.FromArgb(0xE6, 0x8A, 0x00);
        return Color.FromArgb(0x1F, 0xA4, 0x63);
    }

    private static void DrawNumber(Graphics g, int size, double fraction, AppSettings settings, bool stale)
    {
        var percent = (int)Math.Round(fraction * 100);
        var text = percent >= 100 ? "!" : percent.ToString();
        var rect = new RectangleF(0.5f, 0.5f, size - 1, size - 1);
        using (var path = RoundedRect(rect, size * 0.22f))
        using (var brush = new SolidBrush(Severity(fraction, settings, stale)))
            g.FillPath(brush, path);

        var fontSize = size * (text.Length >= 2 ? 0.62f : 0.78f);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var white = new SolidBrush(Color.White);
        var textRect = new RectangleF(-2, size * 0.02f, size + 4, size);
        g.DrawString(text, font, white, textRect, format);
    }

    private static void DrawRing(Graphics g, int size, double? fraction, AppSettings settings, bool stale, bool appIcon = false)
    {
        var dark = appIcon || WindowsTheme.TaskbarIsDark;
        var stroke = Math.Max(2f, size * 0.16f);
        var inset = stroke / 2 + size * 0.06f;
        var rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
        var trackColor = appIcon ? Color.FromArgb(0x3A, 0x3A, 0x3E) : dark ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0);

        if (appIcon)
        {
            using var bg = new SolidBrush(Color.FromArgb(0x1C, 0x1C, 0x1E));
            using var path = RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f);
            g.FillPath(bg, path);
        }

        using (var track = new Pen(trackColor, stroke))
            g.DrawEllipse(track, rect);

        var value = Math.Clamp(fraction ?? 0, 0, 1);
        if (value > 0)
        {
            var color = appIcon ? Color.FromArgb(0xD9, 0x77, 0x57) : Severity(value, settings, stale);
            using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, rect, -90, (float)(360 * value));
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
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
}
