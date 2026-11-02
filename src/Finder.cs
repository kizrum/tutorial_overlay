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
    public byte[] Px = Array.Empty<byte>();   // grayscale, row-major, 2x downscaled

    public static Template From(Bitmap b) => new() { W = b.Width / 2, H = b.Height / 2, Px = Gray(b, out _, out _) };

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
        if (!Templates.TryGetValue(f.Template, out var tpl)) return Miss();
        var r = Resolver.Get(a.WithoutFind().Key) ?? Resolver.ResolveNow(a.WithoutFind());
        if (!r.Found || Native.IsIconic(r.Root)) return Miss();
        var frame = Native.FrameRect(r.Root);
        RectangleF b = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height) : frame;
        // search area: the element, or a part of it ("within" is relative to the element's top-left)
        RectangleF area = f.Within is RectangleF w ? new RectangleF(b.X + w.X, b.Y + w.Y, w.Width, w.Height) : b;
        area.Intersect(b);
        if (area.Width < tpl.W * 2 || area.Height < tpl.H * 2) return Miss();
        using var shot = ScreenCapture.CaptureWindow(r.Root, Rectangle.Round(area));
        if (shot == null) return Miss();
        var img = Template.Gray(shot, out int iw, out int ih);
        var (bx, by, score) = Match(img, iw, ih, tpl);
        if (score > f.MaxScore) return Miss();
        // top-left of the match, back in screen space, then relative to the window frame
        var hit = new PointF(area.X + bx * 2, area.Y + by * 2);
        return new FindResult(true, new PointF(hit.X - frame.X, hit.Y - frame.Y), score, last, DateTime.UtcNow);
    }

    /// <summary>Best position by mean absolute difference (0 = identical … 1 = opposite).</summary>
    private static (int x, int y, float score) Match(byte[] img, int iw, int ih, Template t)
    {
        long best = long.MaxValue;
        int bx = 0, by = 0;
        long n = (long)t.W * t.H;
        for (int y = 0; y + t.H <= ih; y++)
            for (int x = 0; x + t.W <= iw; x++)
            {
                long sad = 0;
                for (int ty = 0; ty < t.H && sad < best; ty++)
                {
                    int io = (y + ty) * iw + x, to = ty * t.W;
                    for (int tx = 0; tx < t.W; tx++) sad += Math.Abs(img[io + tx] - t.Px[to + tx]);
                }
                if (sad < best) { best = sad; bx = x; by = y; }
            }
        return (bx, by, best == long.MaxValue ? 1f : (float)(best / (double)(n * 255)));
    }
}
