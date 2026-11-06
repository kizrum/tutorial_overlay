using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TutorialOverlay;

/// <summary>
/// Screen reads. Our own windows are marked WDA_EXCLUDEFROMCAPTURE, so these never see the overlay's
/// drawings or the step card — pixel checks look at the real application.
/// </summary>
internal static class ScreenCapture
{
    public static Color Sample(PointF p, int radius)
    {
        int n = radius * 2 + 1;
        int x = (int)Math.Round(p.X) - radius, y = (int)Math.Round(p.Y) - radius;
        try
        {
            using var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(n, n), CopyPixelOperation.SourceCopy);
            long r = 0, gr = 0, b = 0;
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                {
                    var c = bmp.GetPixel(i, k);
                    r += c.R; gr += c.G; b += c.B;
                }
            int cnt = n * n;
            return Color.FromArgb(255, (int)(r / cnt), (int)(gr / cnt), (int)(b / cnt));
        }
        catch (Exception e)
        {
            Log.Debug("sample failed: " + e.Message);
            return Color.Black;
        }
    }

    /// <summary>
    /// The averaged colour around each point, from one screen read covering them all (one read of the screen
    /// costs about as much as a small one, so nine separate reads cost nine times as much).
    /// </summary>
    public static Color[] SampleMany(IReadOnlyList<PointF> points, int radius)
    {
        var res = new Color[points.Count];
        if (points.Count == 0) return res;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var p in points) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
        var box = Rectangle.FromLTRB((int)Math.Round(l) - radius, (int)Math.Round(t) - radius, (int)Math.Round(r) + radius + 1, (int)Math.Round(b) + radius + 1);
        // a huge target: one read the size of a monitor would cost more than separate small ones
        if ((long)box.Width * box.Height > 4_000_000)
        {
            for (int i = 0; i < points.Count; i++) res[i] = Sample(points[i], radius);
            return res;
        }
        try
        {
            using var bmp = Capture(box);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int i = 0; i < points.Count; i++)
                    {
                        int cx = (int)Math.Round(points[i].X) - box.X, cy = (int)Math.Round(points[i].Y) - box.Y;
                        long sr = 0, sg = 0, sb = 0, n = 0;
                        for (int y = cy - radius; y <= cy + radius; y++)
                            for (int x = cx - radius; x <= cx + radius; x++)
                            {
                                if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) continue;
                                byte* px = (byte*)data.Scan0 + y * data.Stride + x * 4;
                                sb += px[0]; sg += px[1]; sr += px[2]; n++;
                            }
                        res[i] = n == 0 ? Color.Black : Color.FromArgb(255, (int)(sr / n), (int)(sg / n), (int)(sb / n));
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception e)
        {
            Log.Debug("sample failed: " + e.Message);
            for (int i = 0; i < res.Length; i++) res[i] = Color.Black;
        }
        return res;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    private const uint PW_RENDERFULLCONTENT = 2;

    /// <summary>The last whole-window print of one window: shared by everyone who reads that window's pixels.</summary>
    private sealed class WindowShot
    {
        public Bitmap? Bmp;
        public Rectangle Win;
        public long At;
    }

    private static readonly Dictionary<IntPtr, WindowShot> Shots = new();
    /// <summary>A print younger than this is reused: the finder, OCR, scroll watch and mascot often want the same window together.</summary>
    public const int ShotTtlMs = 90;
    /// <summary>Prints unused this long are let go (a full-window print of a 4K window is ~33 MB).</summary>
    private const int ShotKeepMs = 3000;

    /// <summary>
    /// The window's own pixels (PrintWindow), even while other windows cover it, cropped to <paramref name="screenRect"/>.
    /// Null if the window can't render itself this way (minimised, blank result).
    /// PrintWindow always renders the whole window, so the print is kept for <paramref name="maxAgeMs"/> and other
    /// reads of the same window crop from it instead of printing it again; <paramref name="maxAgeMs"/> 0 = a new print.
    /// </summary>
    public static Bitmap? CaptureWindow(IntPtr hwnd, Rectangle screenRect, int maxAgeMs = ShotTtlMs)
    {
        if (!Native.GetWindowRect(hwnd, out var wr)) return null;
        var win = wr.ToRectangle();
        if (win.Width <= 0 || win.Height <= 0) return null;
        var crop = Rectangle.Intersect(new Rectangle(screenRect.X - win.X, screenRect.Y - win.Y, screenRect.Width, screenRect.Height),
                                       new Rectangle(0, 0, win.Width, win.Height));
        if (crop.Width <= 0 || crop.Height <= 0) return null;
        long now = Environment.TickCount64;
        WindowShot e;
        lock (Shots)
        {
            if (!Shots.TryGetValue(hwnd, out e!)) Shots[hwnd] = e = new WindowShot { At = now };
            // let go of prints nobody has asked for lately (busy ones are skipped, they are in use)
            foreach (var (h, old) in Shots)
            {
                if (ReferenceEquals(old, e) || now - old.At < ShotKeepMs || !System.Threading.Monitor.TryEnter(old)) continue;
                try { old.Bmp?.Dispose(); old.Bmp = null; }
                finally { System.Threading.Monitor.Exit(old); }
            }
            foreach (var h in Shots.Where(kv => kv.Value.Bmp == null && now - kv.Value.At >= ShotKeepMs && !ReferenceEquals(kv.Value, e)).Select(kv => kv.Key).ToList()) Shots.Remove(h);
        }
        Bitmap bmp;
        lock (e)
        {
            if (maxAgeMs <= 0 || e.Bmp == null || e.Win != win || now - e.At > maxAgeMs)
            {
                // the same size as last time: print into the same bitmap instead of allocating ~W×H×4 bytes again
                if (e.Bmp == null || e.Bmp.Width != win.Width || e.Bmp.Height != win.Height)
                {
                    e.Bmp?.Dispose();
                    e.Bmp = new Bitmap(win.Width, win.Height, PixelFormat.Format32bppArgb);
                }
                bool ok;
                using (var g = Graphics.FromImage(e.Bmp))
                {
                    var hdc = g.GetHdc();
                    try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (!ok) { e.Bmp.Dispose(); e.Bmp = null; return null; }
                StretchVirtualized(hwnd, win, e.Bmp);
                e.Win = win;
                e.At = Environment.TickCount64;
            }
            bmp = e.Bmp.Clone(crop, PixelFormat.Format32bppArgb);
        }
        if (LooksBlank(bmp)) { bmp.Dispose(); return null; }
        return bmp;
    }

    /// <summary>
    /// A window that is not DPI-aware draws at 96 dpi and Windows stretches its picture on a scaled monitor; PrintWindow
    /// hands out the unstretched drawing in the print's top-left corner (at 150 %: two thirds of the size). Stretched
    /// here the same way, so the print matches the window on screen and UIA's physical rectangles.
    /// </summary>
    private static void StretchVirtualized(IntPtr hwnd, Rectangle win, Bitmap print)
    {
        uint dpi;
        try { dpi = Native.GetDpiForWindow(hwnd); } catch { return; }
        if (dpi == 0) return;
        float factor = Native.ScaleAt(new PointF(win.X + win.Width / 2f, win.Y + win.Height / 2f)) * 96f / dpi;
        if (Math.Abs(factor - 1) < 0.05f) return;
        int w = (int)Math.Round(win.Width / factor), h = (int)Math.Round(win.Height / factor);
        if (w <= 0 || h <= 0 || w > print.Width || h > print.Height) return;
        using var drawn = print.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(print);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(drawn, new Rectangle(0, 0, print.Width, print.Height));
        Log.Debug($"capture: 0x{hwnd.ToInt64():X} is DPI-virtualized ({dpi} dpi on a {factor:0.##}× monitor): print stretched");
    }

    /// <summary>Some GPU-composited windows print as solid black: treat that as "couldn't capture".</summary>
    private static bool LooksBlank(Bitmap b)
    {
        int distinct = 0;
        Color first = b.GetPixel(0, 0);
        for (int i = 1; i <= 24 && distinct < 3; i++)
        {
            var c = b.GetPixel(b.Width * (i % 5 + 1) / 7, b.Height * (i / 5 + 1) / 7);
            if (Math.Abs(c.R - first.R) + Math.Abs(c.G - first.G) + Math.Abs(c.B - first.B) > 12) distinct++;
        }
        return distinct < 3 && first.R + first.G + first.B < 30;
    }

    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.X, r.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>PNG bytes, downscaled so the long edge is at most <paramref name="maxEdge"/>.</summary>
    public static byte[] Png(Bitmap src, int maxEdge, out float scale)
    {
        scale = 1f;
        Bitmap img = src;
        int edge = Math.Max(src.Width, src.Height);
        if (maxEdge > 0 && edge > maxEdge)
        {
            scale = maxEdge / (float)edge;
            img = new Bitmap(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)), PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(img);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, img.Width, img.Height);
        }
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        if (!ReferenceEquals(img, src)) img.Dispose();
        return ms.ToArray();
    }
}
