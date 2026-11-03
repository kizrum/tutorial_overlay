using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;

namespace TutorialOverlay;

/// <summary>A small grayscale picture of a piece of UI (e.g. the "C5" label of a piano roll keyboard).</summary>
internal sealed class Template
{
    public int W, H;
    public byte[] Px = Array.Empty<byte>();   // grayscale, row-major, 2x downscaled — for the fast coarse pass
    public int FW, FH;
    public byte[] Full = Array.Empty<byte>(); // grayscale at full size — to pick between look-alike candidates

    public static Template From(Bitmap b) => new()
    {
        W = b.Width / 2, H = b.Height / 2, Px = Gray(b, out _, out _),
        FW = b.Width, FH = b.Height, Full = GrayFull(b),
    };

    public static byte[] GrayFull(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var res = new byte[w * h];
        var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < w; x++, row += 4) res[y * w + x] = (byte)((row[2] * 77 + row[1] * 150 + row[0] * 29) >> 8);
                }
            }
        }
        finally { b.UnlockBits(data); }
        return res;
    }

    /// <summary>2x2-averaged grayscale (halves the work, smooths anti-aliasing differences).</summary>
    public static byte[] Gray(Bitmap b, out int w, out int h)
    {
        w = b.Width / 2; h = b.Height / 2;
        var res = new byte[Math.Max(0, w * h)];
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* p0 = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            byte* row = p0 + (2 * y + dy) * stride + 2 * x * 4;
                            for (int dx = 0; dx < 2; dx++)
                            {
                                byte* px = row + dx * 4;
                                sum += (px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8;
                            }
                        }
                        res[y * w + x] = (byte)(sum / 4);
                    }
            }
        }
        finally { b.UnlockBits(data); }
        return res;
    }
}

/// <summary>Where a template was last found, relative to its window's frame (so marks follow window moves).</summary>
internal sealed record FindResult(bool Found, PointF Rel, float Score, PointF? LastRel, DateTime At);

/// <summary>
/// Visual anchors: finds templates inside the anchor's element using the window's own pixels (PrintWindow),
/// a few times per second, on its own thread. That is what lets marks follow content that scrolls
/// (piano roll keys, playlist bars) in apps whose UIA only exposes big panels.
/// </summary>
internal static class Finder
{
    public static readonly ConcurrentDictionary<string, Template> Templates = new();
    private static readonly ConcurrentDictionary<string, FindResult> Results = new();
    private static readonly ConcurrentDictionary<string, DateTime> TextSearchedAt = new();
    /// <summary>OCR is ~10× the cost of template matching: text anchors are re-read less often.</summary>
    private const int TextIntervalMs = 700;
    private static volatile AnchorSpec[] _wanted = Array.Empty<AnchorSpec>();
    private static readonly AutoResetEvent Wake = new(false);
    private static volatile bool _stop;

    public static void Start() => new Thread(Loop) { IsBackground = true, Name = "template-finder" }.Start();
    public static void Stop() { _stop = true; Wake.Set(); }

    public static void SetWanted(IEnumerable<AnchorSpec> anchors)
    {
        var list = anchors.Where(a => a.Find != null).GroupBy(a => a.Key).Select(g => g.First()).ToArray();
        bool changed = list.Length != _wanted.Length;
        _wanted = list;
        if (changed) Wake.Set();
    }

    public static FindResult? Get(string key) => Results.TryGetValue(key, out var r) ? r : null;

    private static void Loop()
    {
        while (!_stop)
        {
            foreach (var a in _wanted)
            {
                if (a.Find!.Text != null)
                {
                    var now = DateTime.UtcNow;
                    if (TextSearchedAt.TryGetValue(a.Key, out var at) && (now - at).TotalMilliseconds < TextIntervalMs) continue;
                    TextSearchedAt[a.Key] = now;
                }
                try { Results[a.Key] = Search(a, Get(a.Key)); }
                catch (Exception e) { Log.Debug("finder: " + e.Message); }
            }
            Wake.WaitOne(_wanted.Length == 0 ? 500 : 180);
        }
    }

    /// <summary>Synchronous search (template tool, screenshots).</summary>
    public static FindResult Search(AnchorSpec a, FindResult? prev)
    {
        var f = a.Find!;
        PointF? last = prev?.Found == true ? prev.Rel : prev?.LastRel;
        FindResult Miss() => new(false, default, 1, last, DateTime.UtcNow);
        Template? tpl = null;
        if (f.Text == null && !Templates.TryGetValue(f.Template, out tpl)) return Miss();
        var r = Resolver.Get(a.WithoutFind().Key) ?? Resolver.ResolveNow(a.WithoutFind());
        if (!r.Found || Native.IsIconic(r.Root)) return Miss();
        var frame = Native.FrameRect(r.Root);
        RectangleF b = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height) : frame;
        // search area: the element, or a part of it ("within" is relative to the element's top-left)
        RectangleF area = f.Within is RectangleF w ? new RectangleF(b.X + w.X, b.Y + w.Y, w.Width, w.Height) : b;
        area.Intersect(b);
        if (tpl != null && (area.Width < tpl.W * 2 || area.Height < tpl.H * 2)) return Miss();
        if (area.Width < 4 || area.Height < 4) return Miss();
        using var shot = ScreenCapture.CaptureWindow(r.Root, Rectangle.Round(area));
        if (shot == null) return Miss();
        if (tpl == null)
        {
            // text: the centre of the n-th occurrence, reading order
            var found = Ocr.Find(Ocr.Read(shot, f.Lang, f.Text), f.Text!, f.TextMatch)
                .OrderBy(m => Math.Round(m.Rect.Y / 8)).ThenBy(m => m.Rect.X).ToList();
            if (found.Count <= f.Nth) return Miss();
            var m = found[f.Nth].Rect;
            var c = new PointF(area.X + m.X + m.Width / 2, area.Y + m.Y + m.Height / 2);
            return new FindResult(true, new PointF(c.X - frame.X, c.Y - frame.Y), found[f.Nth].Score, last, DateTime.UtcNow);
        }
        var img = Template.Gray(shot, out int iw, out int ih);
        var (bx, by, score) = Match(img, iw, ih, tpl, Template.GrayFull(shot), shot.Width, shot.Height);
        if (score > f.MaxScore) return Miss();
        // top-left of the match (full-size pixels), back in screen space, then relative to the window frame
        var hit = new PointF(area.X + bx, area.Y + by);
        return new FindResult(true, new PointF(hit.X - frame.X, hit.Y - frame.Y), score, last, DateTime.UtcNow);
    }

    private const int Candidates = 8, Refine = 3;

    /// <summary>
    /// Best position (full-size pixels) by mean absolute difference (0 = identical … 1 = opposite).
    /// The half-size pass is fast but blurs small differences — "808 Kick" and "808 Snare" buttons score
    /// almost alike, and the 2×2 grid may not line up with the template — so the best few half-size
    /// candidates are compared again at full size, ±3 px around each.
    /// </summary>
    private static (int x, int y, float score) Match(byte[] img, int iw, int ih, Template t, byte[] full, int fw, int fh)
    {
        int mw = iw - t.W + 1, mh = ih - t.H + 1;
        if (mw <= 0 || mh <= 0) return (0, 0, 1f);
        var map = new long[mw * mh];
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                long sad = 0;
                for (int ty = 0; ty < t.H; ty++)
                {
                    int io = (y + ty) * iw + x, to = ty * t.W;
                    for (int tx = 0; tx < t.W; tx++) sad += Math.Abs(img[io + tx] - t.Px[to + tx]);
                }
                map[y * mw + x] = sad;
            }

        // the best few half-size positions, at least half a template apart
        var picked = new List<(int x, int y)>();
        var taken = new bool[map.Length];
        int rx = Math.Max(1, t.W / 2), ry = Math.Max(1, t.H / 2);
        for (int k = 0; k < Candidates; k++)
        {
            int bi = -1;
            for (int i = 0; i < map.Length; i++)
                if (!taken[i] && (bi < 0 || map[i] < map[bi])) bi = i;
            if (bi < 0) break;
            int cx = bi % mw, cy = bi / mw;
            picked.Add((cx, cy));
            for (int y = Math.Max(0, cy - ry); y <= Math.Min(mh - 1, cy + ry); y++)
                for (int x = Math.Max(0, cx - rx); x <= Math.Min(mw - 1, cx + rx); x++) taken[y * mw + x] = true;
        }

        // full-size comparison around each candidate
        long best = long.MaxValue;
        int bx = 0, by = 0;
        foreach (var (cx, cy) in picked)
            for (int y = Math.Max(0, cy * 2 - Refine); y <= Math.Min(fh - t.FH, cy * 2 + Refine); y++)
                for (int x = Math.Max(0, cx * 2 - Refine); x <= Math.Min(fw - t.FW, cx * 2 + Refine); x++)
                {
                    long sad = 0;
                    for (int ty = 0; ty < t.FH && sad < best; ty++)
                    {
                        int io = (y + ty) * fw + x, to = ty * t.FW;
                        for (int tx = 0; tx < t.FW; tx++) sad += Math.Abs(full[io + tx] - t.Full[to + tx]);
                    }
                    if (sad < best) { best = sad; bx = x; by = y; }
                }
        long n = (long)t.FW * t.FH;
        return (bx, by, best == long.MaxValue ? 1f : (float)(best / (double)(n * 255)));
    }
}
