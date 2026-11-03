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
/// <summary>Where a template / text was last found; PrevRel is the sighting before LastRel (which way it was moving).</summary>
internal sealed record FindResult(bool Found, PointF Rel, float Score, PointF? LastRel, DateTime At, PointF? PrevRel = null, DateTime LastSeenAt = default, bool Moving = false, string? Went = null);

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
    private const int TextIntervalMs = 1200, TextConfirmMs = 1500;
    private static volatile AnchorSpec[] _wanted = Array.Empty<AnchorSpec>();
    private static readonly AutoResetEvent Wake = new(false);
    private static volatile bool _stop;

    public static void Start()
    {
        new Thread(Loop) { IsBackground = true, Name = "template-finder" }.Start();
        new Thread(TrackLoop) { IsBackground = true, Name = "text-tracker", Priority = ThreadPriority.BelowNormal }.Start();
    }
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
                    // followed by the tracker, OCR only confirms it now and then; lost, it looks for it a bit more often
                    var g = Get(a.Key);
                    int every = Tracks.ContainsKey(a.Key) && g is { Found: true } ? TextConfirmMs
                              : g is { Found: false } && (now - g.LastSeenAt).TotalSeconds < 4 ? 350   // just lost: look again soon
                              : TextIntervalMs;
                    if (TextSearchedAt.TryGetValue(a.Key, out var at) && (now - at).TotalMilliseconds < every) continue;
                    // the app is not in front: its marks are hidden anyway — no reading until it is
                    if (!AppInFront(a)) continue;
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
        PointF? before = prev?.Found == true ? prev.LastRel : prev?.PrevRel;
        DateTime seenAt = prev?.Found == true ? prev.At : prev?.LastSeenAt ?? default;
        // was it moving (scrolled) just before it went missing? then it went off; still, it is just unreadable
        Tracks.TryGetValue(a.Key, out var pt);
        string? went = prev?.Found == true ? pt?.Went() : prev?.Went;
        bool moving = prev?.Found == true ? pt != null && (DateTime.UtcNow - pt.MovedAt).TotalSeconds < 1 || went != null
                                          : prev?.Moving ?? false;
        FindResult Miss() => new(false, default, 1, last, DateTime.UtcNow, before, seenAt, moving, went);
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
        // a text the tracker follows only needs confirming: read a band around it, not the whole area
        Track? followed = f.Text != null && prev is { Found: true } && Tracks.TryGetValue(a.Key, out var tk) ? tk : null;
        var readArea = area;
        if (followed != null)
        {
            float pad = followed.H * 5 + 60;
            readArea = RectangleF.Intersect(new RectangleF(area.X, followed.Screen.Y - pad, area.Width, followed.H + 2 * pad), area);
            if (readArea.Height < followed.H) readArea = area;
        }
        var shotAt = DateTime.UtcNow;
        var shot = ScreenCapture.CaptureWindow(r.Root, Rectangle.Round(readArea));
        if (shot == null) return Miss();
        try
        {
        if (tpl == null)
        {
            var found = Ocr.Find(Ocr.Read(shot, f.Lang, f.Text, followed != null ? 1_000_000f : 1_500_000f), f.Text!, f.TextMatch)
                .OrderBy(m => Math.Round(m.Rect.Y / 8)).ThenBy(m => m.Rect.X).ToList();
            if (followed != null && found.Count == 0)
            {
                // not in the band around where it was followed to: read the whole area before calling it gone
                Tracks.TryRemove(a.Key, out _);
                followed = null;
                readArea = area;
                shot.Dispose();
                shotAt = DateTime.UtcNow;
                shot = ScreenCapture.CaptureWindow(r.Root, Rectangle.Round(readArea));
                if (shot == null) return Miss();
                found = Ocr.Find(Ocr.Read(shot, f.Lang, f.Text, 1_500_000f), f.Text!, f.TextMatch)
                    .OrderBy(m => Math.Round(m.Rect.Y / 8)).ThenBy(m => m.Rect.X).ToList();
            }
            RectangleF m;
            if (followed != null)
            {
                // confirming: the occurrence nearest to where it is being followed
                var fc = new PointF(followed.Screen.X + followed.W / 2f - readArea.X, followed.Screen.Y + followed.H / 2f - readArea.Y);
                m = found.OrderBy(x => Math.Abs(x.Rect.X + x.Rect.Width / 2 - fc.X) + Math.Abs(x.Rect.Y + x.Rect.Height / 2 - fc.Y)).First().Rect;
            }
            else
            {
                // text: the centre of the n-th occurrence, reading order
                if (found.Count <= f.Nth) { Tracks.TryRemove(a.Key, out _); return Miss(); }
                m = found[f.Nth].Rect;
            }
            var c = new PointF(readArea.X + m.X + m.Width / 2, readArea.Y + m.Y + m.Height / 2);
            // the tracker moved it since this shot was taken: its position is newer than ours
            if (Tracks.TryGetValue(a.Key, out var live) && live.MovedAt > shotAt && prev is { Found: true }) return prev;
            StartTracking(a.Key, shot, m, readArea, area, r.Root);
            return new FindResult(true, new PointF(c.X - frame.X, c.Y - frame.Y), 0, last, DateTime.UtcNow);
        }
        var img = Template.Gray(shot, out int iw, out int ih);
        var (bx, by, score) = Match(img, iw, ih, tpl, Template.GrayFull(shot), shot.Width, shot.Height);
        if (score > f.MaxScore) return Miss();
        // top-left of the match (full-size pixels), back in screen space, then relative to the window frame
        var hit = new PointF(area.X + bx, area.Y + by);
        return new FindResult(true, new PointF(hit.X - frame.X, hit.Y - frame.Y), score, last, DateTime.UtcNow);
        }
        finally { shot?.Dispose(); }
    }

    // =========================================================== following a found text between OCR reads

    /// <summary>The look of a found text (its own pixels) and where it is now on screen.</summary>
    private sealed class Track
    {
        public byte[] Px = Array.Empty<byte>();
        public int[] Ink = Array.Empty<int>();   // the letters' own pixels: what makes it this word, not background
        public int W, H;
        public Rectangle Screen;       // where the text is now
        public RectangleF View;        // the area it lives in (the element / within): never followed outside it
        public IntPtr Root;
        public DateTime MovedAt;
        public DateTime RestUntil;     // after a failed search: a short pause instead of searching 30 times a second
        // the content around a text that stays put: it scrolling past means this is a pinned copy (a sticky header)
        public byte[]? PrevImg;
        public Rectangle PrevRegion;
        public DateTime PrevAt;
        public int ContentAcc;         // px the content moved past it (+ down), since it last moved itself
        public DateTime ContentAt;

        /// <summary>A pinned copy disappearing: the real line went the way the content was going.</summary>
        public string? Went() =>
            Math.Abs(ContentAcc) >= H && (DateTime.UtcNow - ContentAt).TotalSeconds < 3 ? (ContentAcc > 0 ? "down" : "up") : null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>The anchor's app is the foreground one (anchors without a process always count).</summary>
    private static bool AppInFront(AnchorSpec a)
    {
        if (a.Process == null) return true;
        int pid = Native.ProcessId(GetForegroundWindow());
        try { return pid != 0 && System.Diagnostics.Process.GetProcessById(pid).ProcessName.Equals(a.Process, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static readonly ConcurrentDictionary<string, Track> Tracks = new();
    private const int TrackMs = 33;
    private const float TrackMaxScore = 0.16f;

    /// <summary>Pixels clearly off the background (the median): the letters.</summary>
    private static int[] InkOf(byte[] px)
    {
        if (px.Length == 0) return Array.Empty<int>();
        var sorted = (byte[])px.Clone();
        Array.Sort(sorted);
        int bg = sorted[sorted.Length / 2];
        var ink = new List<int>();
        for (int i = 0; i < px.Length; i++) if (Math.Abs(px[i] - bg) > 40) ink.Add(i);
        // hardly any ink (very faint text): fall back to every pixel
        return ink.Count >= 12 ? ink.ToArray() : Enumerable.Range(0, px.Length).ToArray();
    }

    /// <param name="shotArea">where <paramref name="shot"/> was taken (screen)</param>
    /// <param name="view">the whole area the text lives in</param>
    private static void StartTracking(string key, Bitmap shot, RectangleF m, RectangleF shotArea, RectangleF view, IntPtr root)
    {
        var crop = Rectangle.Intersect(Rectangle.Round(m), new Rectangle(0, 0, shot.Width, shot.Height));
        if (crop.Width < 6 || crop.Height < 6) return;
        using var part = shot.Clone(crop, PixelFormat.Format32bppArgb);
        var px = Template.GrayFull(part);
        var t = new Track
        {
            Px = px, Ink = InkOf(px), W = crop.Width, H = crop.Height, Root = root, View = view,
            Screen = new Rectangle((int)shotArea.X + crop.X, (int)shotArea.Y + crop.Y, crop.Width, crop.Height),
        };
        // re-confirmed in the same place: keep what was seen of the content scrolling past it
        if (Tracks.TryGetValue(key, out var old) && Math.Abs(old.Screen.Y - t.Screen.Y) <= 3 && Math.Abs(old.Screen.X - t.Screen.X) <= 3)
        {
            t.ContentAcc = old.ContentAcc; t.ContentAt = old.ContentAt; t.MovedAt = old.MovedAt;
        }
        Tracks[key] = t;
    }

    /// <summary>
    /// OCR runs a few times a second — too slow for a highlight to ride along with scrolling text. In between,
    /// the text's own pixels are looked for around where it was, ~30 times a second (plain pixel matching on a
    /// small piece of screen, cheap): a still text costs one comparison, a moving one is followed smoothly, and
    /// the next OCR read confirms it is still the right word. A doubtful match moves nothing.
    /// </summary>
    private static void TrackLoop()
    {
        while (!_stop)
        {
            var wanted = _wanted.Select(a => a.Key).ToHashSet();
            foreach (var key in Tracks.Keys.Where(k => !wanted.Contains(k)).ToList()) Tracks.TryRemove(key, out _);
            foreach (var (key, tr) in Tracks.ToArray())
            {
                try { Follow(key, tr); }
                catch (Exception e) { Log.Debug("tracker: " + e.Message); }
            }
            Thread.Sleep(Tracks.IsEmpty ? 150 : TrackMs);
        }
    }

    private static void Follow(string key, Track tr)
    {
        if (Get(key) is not { Found: true } cur || Native.IsIconic(tr.Root) || DateTime.UtcNow < tr.RestUntil) return;
        // the screen must show the app's own window where the text is (not another window on top, not minimised)
        var mid = new PointF(tr.Screen.X + tr.Screen.Width / 2f, tr.Screen.Y + tr.Screen.Height / 2f);
        if (Native.HitWindow(mid).root != tr.Root) return;
        // look around the last place: far up and down (scrolling), a little sideways
        int my = Math.Max(160, tr.H * 6), mx = Math.Max(40, tr.W / 3);
        var region = Rectangle.Intersect(Rectangle.Inflate(tr.Screen, mx, my), Rectangle.Round(tr.View));
        if (region.Width < tr.W || region.Height < tr.H) return;
        using var shot = ScreenCapture.Capture(region);
        var img = Template.GrayFull(shot);
        int iw = shot.Width, ih = shot.Height;
        int lx = tr.Screen.X - region.X, ly = tr.Screen.Y - region.Y;
        // compared on the letters only: mostly-background templates otherwise "match" any near-empty spot, and a
        // changed background (selection, current-line highlight) would otherwise hide the very word
        float Score(int x, int y, int step)
        {
            if (x < 0 || y < 0 || x + tr.W > iw || y + tr.H > ih) return 1;
            long sad = 0, n = 0;
            for (int i = 0; i < tr.Ink.Length; i += step)
            {
                int o = tr.Ink[i], ty = o / tr.W, tx = o % tr.W;
                sad += Math.Abs(img[(y + ty) * iw + x + tx] - tr.Px[o]);
                n++;
            }
            return n == 0 ? 1 : sad / (float)(n * 255);
        }
        // still where it was: only keep an eye on whether the content scrolls past it
        if (Score(lx, ly, 1) < TrackMaxScore * 0.5f) { WatchContent(tr, img, iw, ih, region, ly, false); return; }
        WatchContent(tr, img, iw, ih, region, ly, true);
        int bx = -1, by = -1;
        float best = float.MaxValue;
        void Scan(int x0, int x1)
        {
            for (int y = 0; y + tr.H <= ih; y += 2)
                for (int x = Math.Max(0, x0); x <= Math.Min(iw - tr.W, x1); x += 2)
                {
                    float v = Score(x, y, 3);
                    if (v < best) { best = v; bx = x; by = y; }
                }
        }
        // scrolling moves text up or down: its own column first (a few hundred positions), the whole
        // neighbourhood only if that finds nothing good
        Scan(lx - 4, lx + 4);
        if (best > TrackMaxScore * 1.3f) Scan(0, iw - tr.W);
        if (best > TrackMaxScore * 1.3f) { FollowFar(key, tr, cur); return; }
        if (bx < 0) return;
        int fx = bx, fy = by;
        best = float.MaxValue;
        for (int y = by - 2; y <= by + 2; y++)
            for (int x = bx - 2; x <= bx + 2; x++)
            {
                float v = Score(x, y, 1);
                if (v < best) { best = v; fx = x; fy = y; }
            }
        if (best > TrackMaxScore) { Lost(key, tr, cur); return; }
        var moved = new Rectangle(region.X + fx, region.Y + fy, tr.W, tr.H);
        if (moved.Location == tr.Screen.Location) return;
        tr.ContentAcc = 0;
        tr.PrevImg = null;
        tr.Screen = moved;
        tr.MovedAt = DateTime.UtcNow;
        var frame = Native.FrameRect(tr.Root);
        var c = new PointF(moved.X + moved.Width / 2f, moved.Y + moved.Height / 2f);
        Results[key] = new FindResult(true, new PointF(c.X - frame.X, c.Y - frame.Y), cur.Score, cur.Rel, DateTime.UtcNow);
    }

    /// <summary>
    /// A fast flick moves the text further than the near search looks: its own column over the whole area.
    /// </summary>
    private static void FollowFar(string key, Track tr, FindResult cur)
    {
        var col = Rectangle.Intersect(new Rectangle(tr.Screen.X - 6, (int)tr.View.Y, tr.W + 12, (int)tr.View.Height), Rectangle.Round(tr.View));
        if (col.Width < tr.W || col.Height < tr.H) { Lost(key, tr, cur); return; }
        using var shot = ScreenCapture.Capture(col);
        var img = Template.GrayFull(shot);
        int iw = shot.Width, ih = shot.Height;
        float Score(int x, int y, int step)
        {
            if (x < 0 || y < 0 || x + tr.W > iw || y + tr.H > ih) return 1;
            long sad = 0, n = 0;
            for (int i = 0; i < tr.Ink.Length; i += step)
            {
                int o = tr.Ink[i];
                sad += Math.Abs(img[(y + o / tr.W) * iw + x + o % tr.W] - tr.Px[o]);
                n++;
            }
            return n == 0 ? 1 : sad / (float)(n * 255);
        }
        float best = float.MaxValue; int bx = 0, by = 0;
        for (int y = 0; y + tr.H <= ih; y += 2)
            for (int x = 0; x + tr.W <= iw; x += 2)
            {
                float v = Score(x, y, 3);
                if (v < best) { best = v; bx = x; by = y; }
            }
        int fx = bx, fy = by;
        best = float.MaxValue;
        for (int y = by - 2; y <= by + 2; y++)
            for (int x = bx - 2; x <= bx + 2; x++)
            {
                float v = Score(x, y, 1);
                if (v < best) { best = v; fx = x; fy = y; }
            }
        if (best > TrackMaxScore) { Lost(key, tr, cur); return; }
        var moved = new Rectangle(col.X + fx, col.Y + fy, tr.W, tr.H);
        tr.ContentAcc = 0;
        tr.PrevImg = null;
        tr.Screen = moved;
        tr.MovedAt = DateTime.UtcNow;
        var frame = Native.FrameRect(tr.Root);
        var c = new PointF(moved.X + moved.Width / 2f, moved.Y + moved.Height / 2f);
        Results[key] = new FindResult(true, new PointF(c.X - frame.X, c.Y - frame.Y), cur.Score, cur.Rel, DateTime.UtcNow);
    }

    /// <summary>
    /// The tracker cannot see it any more: no mark at a stale place. Say it is not found (and whether it was
    /// moving, for which way it went) and have OCR look over the whole area right away.
    /// </summary>
    private static void Lost(string key, Track tr, FindResult cur)
    {
        string? went = tr.Went();
        bool moving = (DateTime.UtcNow - tr.MovedAt).TotalSeconds < 1 || went != null;
        Tracks.TryRemove(key, out _);
        Results[key] = new FindResult(false, default, 1, cur.Rel, DateTime.UtcNow, cur.LastRel, cur.At, moving, went);
        TextSearchedAt.TryRemove(key, out _);
        Wake.Set();
    }

    /// <summary>
    /// The text stays put: does the content next to it (rows below it, or above near the bottom) scroll? A text
    /// that stays while its neighbourhood scrolls is a pinned copy, and the real line moves with the content.
    /// Costs one comparison while nothing changes; ~10 a second while something does.
    /// </summary>
    private static void WatchContent(Track tr, byte[] img, int iw, int ih, Rectangle region, int ly, bool force)
    {
        var now = DateTime.UtcNow;
        if (tr.PrevImg == null || tr.PrevRegion != region || tr.PrevImg.Length != img.Length)
        {
            tr.PrevImg = img; tr.PrevRegion = region; tr.PrevAt = now;
            return;
        }
        if (!force && (now - tr.PrevAt).TotalMilliseconds < 90) return;
        int below = ih - (ly + tr.H + 2), above = ly - 2;
        int y0 = below >= above ? ly + tr.H + 2 : 0, y1 = below >= above ? ih : above;
        if (y1 - y0 >= 24)
        {
            int d = BandShift(tr.PrevImg, img, iw, y0, y1);
            if (d != 0) { tr.ContentAcc += d; tr.ContentAt = now; }
        }
        tr.PrevImg = img; tr.PrevAt = now;
    }

    /// <summary>Vertical shift of rows y0..y1 between two looks (+ = moved down; 0 = still or not a scroll).</summary>
    private static int BandShift(byte[] a, byte[] b, int w, int y0, int y1)
    {
        int h = y1 - y0, max = h / 2;
        double Diff(int d)
        {
            long sum = 0, n = 0;
            for (int y = Math.Max(0, d); y < Math.Min(h, h + d); y += 2)
            {
                int ra = (y0 + y - d) * w, rb = (y0 + y) * w;
                for (int x = 0; x < w; x += 3) { sum += Math.Abs(a[ra + x] - b[rb + x]); n++; }
            }
            return n < 40 ? double.MaxValue : sum / (double)n;
        }
        double still = Diff(0);
        if (still < 2) return 0;
        int best = 0;
        double bestDiff = still;
        for (int d = -max; d <= max; d++)
        {
            if (d == 0) continue;
            double v = Diff(d);
            if (v < bestDiff) { bestDiff = v; best = d; }
        }
        return bestDiff < still * 0.5 && bestDiff < 12 ? best : 0;
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
