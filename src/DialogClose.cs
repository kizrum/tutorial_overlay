using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace TutorialOverlay;

/// <summary>
/// Where a window in the way is closed: its close button, found by the overlay itself so the lesson can point at it
/// ("close it here") instead of framing the whole window. Tried in order: the button UI Automation names Close; the
/// spot Windows itself reports as the close box (WM_NCHITTEST → HTCLOSE, any standard title bar); else the "X" drawn
/// at the right of the window's top strip (Steam, Chromium apps — their title bars are pictures). Looked up once per
/// window, off the frame loop; until it is known (or when there is none) the caller frames the window.
/// </summary>
internal static class DialogClose
{
    private static readonly object Gate = new();
    // the close spot relative to the window's frame, kept while the window keeps its size
    private static readonly Dictionary<IntPtr, (Size size, RectangleF? rel)> Known = new();
    private static readonly HashSet<IntPtr> Looking = new();

    /// <summary>The close spot of <paramref name="hwnd"/> in screen pixels, or null (not known yet, or none).</summary>
    public static RectangleF? For(IntPtr hwnd, Rectangle frame)
    {
        lock (Gate)
        {
            if (Known.TryGetValue(hwnd, out var k) && k.size == frame.Size)
                return k.rel is RectangleF r ? new RectangleF(r.X + frame.X, r.Y + frame.Y, r.Width, r.Height) : null;
            if (!Looking.Add(hwnd)) return null;
            if (Known.Count > 64) Known.Clear();
        }
        Task.Run(() =>
        {
            RectangleF? spot = null;
            try { spot = Find(hwnd, frame); }
            catch (Exception e) { Log.Debug("close spot: " + e.Message); }
            lock (Gate)
            {
                Known[hwnd] = (frame.Size, spot is RectangleF s ? new RectangleF(s.X - frame.X, s.Y - frame.Y, s.Width, s.Height) : null);
                Looking.Remove(hwnd);
            }
            Log.Debug($"close spot of 0x{hwnd.ToInt64():X}: {(spot is RectangleF f ? $"{f.X:0},{f.Y:0} {f.Width:0}x{f.Height:0}" : "none")}");
        });
        return null;
    }

    private static RectangleF? Find(IntPtr hwnd, Rectangle frame)
    {
        float k = Native.ScaleAt(new PointF(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f));
        return ByHitTest(hwnd, frame, k) ?? ByUia(hwnd, frame, k) ?? ByPicture(hwnd, frame, k);
    }

    // ---- Windows' own close box

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);

    private const uint WM_NCHITTEST = 0x84, SMTO_ABORTIFHUNG = 2;
    private const int HTCLOSE = 20;

    private static int HitTest(IntPtr hwnd, int x, int y)
    {
        var l = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        return SendMessageTimeout(hwnd, WM_NCHITTEST, IntPtr.Zero, l, SMTO_ABORTIFHUNG, 150, out var res) == IntPtr.Zero ? 0 : (int)res;
    }

    private static RectangleF? ByHitTest(IntPtr hwnd, Rectangle frame, float k)
    {
        int step = Math.Max(2, (int)(3 * k));
        int right = frame.Right - 1, top = frame.Top + 1;
        int maxX = Math.Min(frame.Width - 2, (int)(160 * k)), maxY = Math.Min(frame.Height - 2, (int)(48 * k));
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (int dy = 0; dy <= maxY; dy += step)
            for (int dx = 0; dx <= maxX; dx += step)
                if (HitTest(hwnd, right - dx, top + dy) == HTCLOSE)
                {
                    x0 = Math.Min(x0, right - dx); x1 = Math.Max(x1, right - dx);
                    y0 = Math.Min(y0, top + dy); y1 = Math.Max(y1, top + dy);
                }
        if (x0 == int.MaxValue) return null;
        return new RectangleF(x0, y0, x1 - x0 + step, y1 - y0 + step);
    }

    // ---- UI Automation's Close button

    private static readonly string[] CloseNames = { "close", "закрыть", "×", "✕", "x" };

    private static RectangleF? ByUia(IntPtr hwnd, Rectangle frame, float k)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        RectangleF? best = null;
        foreach (AutomationElement b in buttons)
        {
            AutomationElement.AutomationElementInformation c;
            try { c = b.Current; } catch { continue; }
            if (c.IsOffscreen) continue;
            var r = c.BoundingRectangle;
            if (r.IsEmpty || r.Width < 4 || r.Height < 4) continue;
            // in the window's top strip: a "Close" button lower down ends a form, not the window
            if (r.Top > frame.Top + 64 * k || r.Width > 120 * k || r.Height > 80 * k) continue;
            string name = (c.Name ?? "").Trim().ToLowerInvariant(), id = (c.AutomationId ?? "").ToLowerInvariant();
            if (!(id == "close" || CloseNames.Contains(name))) continue;
            var rf = new RectangleF((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
            if (best == null || rf.Right > best.Value.Right) best = rf;
        }
        return best;
    }

    // ---- the "X" drawn in the top strip

    private static RectangleF? ByPicture(IntPtr hwnd, Rectangle frame, float k)
    {
        int w = Math.Min(frame.Width, (int)(220 * k)), h = Math.Min(frame.Height, (int)(64 * k));
        if (w < 16 || h < 16) return null;
        var strip = new Rectangle(frame.Right - w, frame.Top, w, h);
        using var bmp = ScreenCapture.CaptureWindowOrScreen(hwnd, strip, 0);
        if (bmp == null) return null;
        var glyph = FindCross(bmp, k);
        if (glyph is not RectangleF g) return null;
        g.Offset(strip.X, strip.Y);
        // the button is bigger than its glyph: give the click room around it
        float pad = Math.Max(4 * k, g.Width * 0.45f);
        g.Inflate(pad, pad);
        return g;
    }

    /// <summary>
    /// The rightmost "X" glyph in a title strip: a blob that stands out from the strip's background, about square,
    /// 5–26 px (at 100 %) on a side, its pixels on the two diagonals of its box and on both of them.
    /// </summary>
    internal static RectangleF? FindCross(Bitmap bmp, float k)
    {
        int w = bmp.Width, h = bmp.Height;
        var lum = new int[w * h];
        var hist = new int[64];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[w];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
                for (int x = 0; x < w; x++)
                {
                    int p = row[x];
                    int l = (((p >> 16) & 255) * 299 + ((p >> 8) & 255) * 587 + (p & 255) * 114) / 1000;
                    lum[y * w + x] = l;
                    hist[l >> 2]++;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        int bg = (Array.IndexOf(hist, hist.Max()) << 2) + 2;
        var ink = new bool[w * h];
        for (int i = 0; i < ink.Length; i++) ink[i] = Math.Abs(lum[i] - bg) > 38;

        var seen = new bool[w * h];
        var queue = new Queue<int>();
        var pts = new List<(int x, int y)>();
        RectangleF? best = null;
        float minSide = 5 * k, maxSide = 26 * k;
        for (int start = 0; start < ink.Length; start++)
        {
            if (!ink[start] || seen[start]) continue;
            pts.Clear();
            seen[start] = true;
            queue.Enqueue(start);
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(), x = i % w, y = i / w;
                pts.Add((x, y));
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                if (pts.Count > 4000) break;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx;
                        if (ink[j] && !seen[j]) { seen[j] = true; queue.Enqueue(j); }
                    }
            }
            queue.Clear();
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (bw < minSide || bh < minSide || bw > maxSide || bh > maxSide) continue;
            float aspect = bw / (float)bh;
            if (aspect < 0.7f || aspect > 1.45f) continue;
            if (pts.Count < 1.2f * Math.Max(bw, bh) || pts.Count > 0.6f * bw * bh) continue;
            int near = 0, onMain = 0, onAnti = 0, middle = 0;
            foreach (var (x, y) in pts)
            {
                float u = (x - x0 + 0.5f) / bw, v = (y - y0 + 0.5f) / bh;
                float d1 = Math.Abs(u - v), d2 = Math.Abs(u + v - 1);
                if (Math.Min(d1, d2) < 0.2f) near++;
                if (d1 < 0.2f) onMain++;
                if (d2 < 0.2f) onAnti++;
                if (Math.Abs(u - 0.5f) < 0.18f && Math.Abs(v - 0.5f) < 0.18f) middle++;
            }
            if (near < 0.85f * pts.Count || onMain < 0.3f * pts.Count || onAnti < 0.3f * pts.Count || middle == 0) continue;
            // the arms reach the box's corners: an "X", not a blob or a diamond
            bool Corner(float cu, float cv) => pts.Any(p => Math.Abs((p.x - x0 + 0.5f) / bw - cu) < 0.22f && Math.Abs((p.y - y0 + 0.5f) / bh - cv) < 0.22f);
            if (!(Corner(0.1f, 0.1f) && Corner(0.9f, 0.1f) && Corner(0.1f, 0.9f) && Corner(0.9f, 0.9f))) continue;
            var r = new RectangleF(x0, y0, bw, bh);
            if (best == null || r.Right > best.Value.Right) best = r;
        }
        return best;
    }
}

/// <summary>A window of the app in the lesson's way: <paramref name="modal"/> when the app ignores clicks meanwhile.</summary>
internal sealed record Blocker(IntPtr hwnd, string title, Rectangle frame, bool modal);
