using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace TutorialOverlay;

/// <summary>
/// Icons in a picture of an app that draws its own UI (FL Studio's Play / Stop / Record): compact marks with no text
/// and no UIA name. Found from edges alone — a blob of strong contrast changes, icon-sized and roughly compact — and
/// told apart from look-alikes (a hundred identical step buttons or knobs) by a small signature: only an icon that
/// occurs once can get a template anchor that finds it and nothing else.
/// </summary>
internal static class Icons
{
    /// <summary>Contrast between neighbouring pixels that counts as an edge (of 255).</summary>
    public const int EdgeContrast = 40;
    public const int MinSide = 8, MaxSide = 64;

    /// <summary>
    /// Icon-like blobs in a grayscale picture (w × h): bounding boxes of connected edge pixels, parts closer than a
    /// few pixels joined (a pause sign is two bars), kept when icon-sized, not too long and thin, and not hollow
    /// frames or solid blocks.
    /// </summary>
    public static List<Rectangle> FindBlobs(byte[] gray, int w, int h)
    {
        // edge mask at half size (a 2×2 cell is an edge if any pixel in it is): halves the work, joins 1 px gaps
        int mw = w / 2, mh = h / 2;
        var mask = new bool[mw * mh];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                int dx = Math.Abs(gray[i + 1] - gray[i - 1]), dy = Math.Abs(gray[i + w] - gray[i - w]);
                if (Math.Max(dx, dy) < EdgeContrast) continue;
                int mx = x / 2, my = y / 2;
                if (mx < mw && my < mh) mask[my * mw + mx] = true;
            }

        // grown by one cell (≈2 px): the parts of one icon a few pixels apart (a pause sign's two bars) become one blob
        var grown = new bool[mask.Length];
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
                if (mask[y * mw + x])
                    for (int ny = Math.Max(0, y - 1); ny <= Math.Min(mh - 1, y + 1); ny++)
                        for (int nx = Math.Max(0, x - 1); nx <= Math.Min(mw - 1, x + 1); nx++) grown[ny * mw + nx] = true;
        var edges = mask;
        mask = grown;

        // connected components (8 neighbours) of the mask
        var boxes = new List<(Rectangle box, int count)>();
        var seen = new bool[mask.Length];
        var stack = new Stack<int>();
        for (int s = 0; s < mask.Length; s++)
        {
            if (!mask[s] || seen[s]) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
            bool tooBig = false;
            seen[s] = true;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % mw, y = i / mw;
                // the box is that of the edges themselves: the growth only joins parts (a speck stays a speck)
                if (edges[i])
                {
                    count++;
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
                if (x1 - x0 > MaxSide || y1 - y0 > MaxSide) tooBig = true;   // a frame or a panel border: keep walking it, drop it after
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(mh - 1, y + 1); ny++)
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(mw - 1, x + 1); nx++)
                    {
                        int j = ny * mw + nx;
                        if (mask[j] && !seen[j]) { seen[j] = true; stack.Push(j); }
                    }
            }
            if (!tooBig) boxes.Add((new Rectangle(x0 * 2, y0 * 2, (x1 - x0 + 1) * 2, (y1 - y0 + 1) * 2), count * 4));
        }

        return boxes.Where(b => IconShaped(b.box, b.count)).Select(b => b.box).ToList();
    }

    private static bool IconShaped(Rectangle r, int edgePixels)
    {
        if (r.Width < MinSide || r.Height < MinSide || r.Width > MaxSide || r.Height > MaxSide) return false;
        float aspect = r.Width / (float)r.Height;
        if (aspect > 3 || aspect < 1 / 3f) return false;          // a line, a slider track
        float fill = edgePixels / (float)(r.Width * r.Height);
        return fill > 0.08f && fill < 0.95f;                      // not a speck, not a solid block of edges (noise, texture)
    }

    /// <summary>
    /// The visible extent of what lies under a point (a caption's letters, a button's frame): edges near the point,
    /// joined across small gaps (letters of a word, parts of a frame), the group the point is in — its box, padded a
    /// little. Null when nothing is drawn near the point, or when the group is a whole panel (bigger than max).
    /// </summary>
    public static Rectangle? TargetBox(byte[] gray, int w, int h, Point p, int join = 3, int max = 320, int contrast = EdgeContrast)
    {
        var win = Rectangle.Intersect(new Rectangle(p.X - max, p.Y - max, 2 * max, 2 * max), new Rectangle(0, 0, w, h));
        int ww = win.Width, wh = win.Height;
        if (ww < 3 || wh < 3) return null;
        var edge = new bool[ww * wh];
        for (int y = 1; y < wh - 1; y++)
            for (int x = 1; x < ww - 1; x++)
            {
                int i = (win.Y + y) * w + win.X + x;
                if (Math.Max(Math.Abs(gray[i + 1] - gray[i - 1]), Math.Abs(gray[i + w] - gray[i - w])) >= contrast) edge[y * ww + x] = true;
            }
        // the seed: the edge pixel nearest to the point, within a few pixels (a point between two letters still counts)
        int px = p.X - win.X, py = p.Y - win.Y, seed = -1, bestD = int.MaxValue;
        for (int y = Math.Max(0, py - 10); y <= Math.Min(wh - 1, py + 10); y++)
            for (int x = Math.Max(0, px - 10); x <= Math.Min(ww - 1, px + 10); x++)
                if (edge[y * ww + x]) { int d = (x - px) * (x - px) + (y - py) * (y - py); if (d < bestD) { bestD = d; seed = y * ww + x; } }
        if (seed < 0) return null;
        // flood over edges, jumping gaps up to `join` pixels
        var seen = new bool[edge.Length];
        var stack = new Stack<int>();
        stack.Push(seed); seen[seed] = true;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        while (stack.Count > 0)
        {
            int i = stack.Pop(), x = i % ww, y = i / ww;
            if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
            if (x1 - x0 > max || y1 - y0 > max) return null;   // a panel border: not a target of its own
            for (int ny = Math.Max(0, y - join); ny <= Math.Min(wh - 1, y + join); ny++)
                for (int nx = Math.Max(0, x - join); nx <= Math.Min(ww - 1, x + join); nx++)
                {
                    int j = ny * ww + nx;
                    if (edge[j] && !seen[j]) { seen[j] = true; stack.Push(j); }
                }
        }
        var box = new Rectangle(win.X + x0, win.Y + y0, x1 - x0 + 1, y1 - y0 + 1);
        box.Inflate(3, 3);
        return Rectangle.Intersect(box, new Rectangle(0, 0, w, h));
    }

    /// <summary>
    /// An 8×8 picture of the box, its mean taken out, and the box's mean colour (rgb: 3 bytes a pixel, optional):
    /// what tells an icon from its look-alikes — FL's green Play triangle has the shape of a grey expand arrow.
    /// </summary>
    public static int[] Signature(byte[] gray, int w, int h, Rectangle r, byte[]? rgb = null)
    {
        var sig = new int[67];
        for (int cy = 0; cy < 8; cy++)
            for (int cx = 0; cx < 8; cx++)
            {
                int xa = r.X + cx * r.Width / 8, xb = Math.Max(xa + 1, r.X + (cx + 1) * r.Width / 8);
                int ya = r.Y + cy * r.Height / 8, yb = Math.Max(ya + 1, r.Y + (cy + 1) * r.Height / 8);
                int sum = 0, n = 0;
                for (int y = ya; y < yb && y < h; y++)
                    for (int x = xa; x < xb && x < w; x++) { sum += gray[y * w + x]; n++; }
                sig[cy * 8 + cx] = n > 0 ? sum / n : 0;
            }
        int mean = (int)sig.Take(64).Average();
        for (int i = 0; i < 64; i++) sig[i] -= mean;
        if (rgb != null)
        {
            // the colour of the ink, not of the box: the pixels that stand out from the background along its border
            // (a small green triangle on a dark button is green, though most of its box is dark)
            int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y), x1 = Math.Min(w, r.Right) - 1, y1 = Math.Min(h, r.Bottom) - 1;
            long bg = 0, bn = 0;
            for (int x = x0; x <= x1; x++) { bg += gray[y0 * w + x] + gray[y1 * w + x]; bn += 2; }
            for (int y = y0; y <= y1; y++) { bg += gray[y * w + x0] + gray[y * w + x1]; bn += 2; }
            int back = bn > 0 ? (int)(bg / bn) : 0;
            long cr = 0, cg = 0, cb = 0, n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (Math.Abs(gray[y * w + x] - back) < 30) continue;
                    int o = (y * w + x) * 3; cr += rgb[o]; cg += rgb[o + 1]; cb += rgb[o + 2]; n++;
                }
            if (n > 0) { sig[64] = (int)(cr / n); sig[65] = (int)(cg / n); sig[66] = (int)(cb / n); }
        }
        return sig;
    }

    /// <summary>Mean absolute difference of two signatures, for boxes of about the same size; else "different".</summary>
    public static float Difference(int[] a, Rectangle ra, int[] b, Rectangle rb)
    {
        // look-alikes drawn twice are the same size to a pixel or two (step buttons, knobs); 16×24 is not 12×20
        if (Math.Abs(ra.Width - rb.Width) > SizeSlack(ra.Width) || Math.Abs(ra.Height - rb.Height) > SizeSlack(ra.Height)) return 255;
        // another colour is another icon (a red Record circle is not a grey slot circle)
        for (int c = 64; c < 67 && c < a.Length && c < b.Length; c++) if (Math.Abs(a[c] - b[c]) > MaxTint) return 255;
        int d = 0;
        for (int i = 0; i < 64; i++) d += Math.Abs(a[i] - b[i]);
        return d / 64f;
    }

    private static int SizeSlack(int side) => Math.Max(2, side / 10);

    /// <summary>Signatures closer than this are the same icon drawn twice (a step button, a knob).</summary>
    public const float SameIcon = 12;
    /// <summary>Mean colour differing more than this in a channel: not the same icon.</summary>
    public const int MaxTint = 40;

    /// <summary>The boxes that have no look-alike among the others.</summary>
    public static List<int> Unique(IReadOnlyList<Rectangle> boxes, IReadOnlyList<int[]> sigs)
    {
        // only boxes of about the same size can be look-alikes: compare within size buckets (4 px) and their neighbours
        var buckets = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < boxes.Count; i++)
        {
            var key = (boxes[i].Width / 4, boxes[i].Height / 4);
            if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<int>();
            l.Add(i);
        }
        var repeated = new bool[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            if (repeated[i]) continue;
            var (bw, bh) = (boxes[i].Width / 4, boxes[i].Height / 4);
            for (int dw = -1; dw <= 1 && !repeated[i]; dw++)
                for (int dh = -1; dh <= 1 && !repeated[i]; dh++)
                    if (buckets.TryGetValue((bw + dw, bh + dh), out var l))
                        foreach (var k in l)
                            if (k != i && Difference(sigs[i], boxes[i], sigs[k], boxes[k]) < SameIcon) { repeated[i] = repeated[k] = true; break; }
        }
        return Enumerable.Range(0, boxes.Count).Where(i => !repeated[i]).ToList();
    }

    /// <summary>
    /// The control a caption sits on: a drop-down with its arrow, a tab, a button, a menu-bar item — what the user
    /// clicks, which is often wider than the words. A control with its own background (a box above and below the
    /// text) spreads to its borders; a caption on a flat bar or list (no box) spreads sideways to the next caption
    /// (halfway into the gap) or the panel's edge, and a little above and below. Null when nothing bounds it (text in
    /// a big page): then the words themselves are the target. <paramref name="t"/> is in picture pixels.
    /// </summary>
    internal static Rectangle? ControlBox(byte[] g, int w, int h, Rectangle t)
    {
        if (t.Width < 2 || t.Height < 4 || t.X < 0 || t.Y < 0 || t.Right > w || t.Bottom > h) return null;
        int th = t.Height;
        var ring = new List<byte>();
        for (int x = Math.Max(0, t.X - 3); x < Math.Min(w, t.Right + 3); x++)
        {
            if (t.Y - 3 >= 0) ring.Add(g[(t.Y - 3) * w + x]);
            if (t.Bottom + 2 < h) ring.Add(g[(t.Bottom + 2) * w + x]);
        }
        for (int y = t.Y; y < t.Bottom; y++)
        {
            if (t.X - 3 >= 0) ring.Add(g[y * w + t.X - 3]);
            if (t.Right + 2 < w) ring.Add(g[y * w + t.Right + 2]);
        }
        if (ring.Count < 8) return null;
        ring.Sort();
        int bg = ring[ring.Count / 2];
        const int Tol = 16;

        // a border or another background: most of the line is off the background, and evenly so (not letters)
        bool Edge(IEnumerable<byte> line)
        {
            int n = 0, off = 0; long sum = 0, sq = 0;
            foreach (var v in line)
            {
                n++;
                if (Math.Abs(v - bg) > Tol) { off++; sum += v; sq += v * v; }
            }
            if (n == 0 || off < n * 0.8) return false;
            double mean = sum / (double)off, sd = Math.Sqrt(Math.Max(0, sq / (double)off - mean * mean));
            return sd < 22;
        }
        bool Ink(IEnumerable<byte> line) { int n = 0, off = 0; foreach (var v in line) { n++; if (Math.Abs(v - bg) > Tol) off++; } return off > 0 && off >= n * 0.12; }
        IEnumerable<byte> Row(int y, int x0, int x1) { for (int x = x0; x < x1; x++) yield return g[y * w + x]; }
        IEnumerable<byte> Col(int x, int y0, int y1) { for (int y = y0; y < y1; y++) yield return g[y * w + x]; }

        // above and below the words: a box of its own?
        int capY = th * 2;
        int top = t.Y, bot = t.Bottom;
        bool topEdge = false, botEdge = false;
        for (int i = 0; i < capY && top > 0; i++) { if (Edge(Row(top - 1, t.X, t.Right))) { topEdge = true; break; } top--; }
        for (int i = 0; i < capY && bot < h; i++) { if (Edge(Row(bot, t.X, t.Right))) { botEdge = true; break; } bot++; }
        bool boxed = topEdge && botEdge;
        if (!boxed) { top = Math.Max(0, t.Y - th / 2); bot = Math.Min(h, t.Bottom + th / 2); }

        int capX = Math.Clamp(th * 14, 120, 360);
        int Side(int dir, out bool bounded)
        {
            int x = dir < 0 ? t.X : t.Right, gapAt = -1;
            bounded = false;
            for (int i = 0; i < capX; i++)
            {
                int nx = dir < 0 ? x - 1 : x;
                if (nx < 0 || nx >= w) break;
                if (boxed)
                {
                    if (Edge(Col(nx, top, bot))) { bounded = true; break; }
                }
                else
                {
                    // a flat bar: the next caption or icon (solid or not) ends this one, halfway into the gap before it
                    bool ink = Edge(Col(nx, t.Y, t.Bottom)) || Ink(Col(nx, t.Y, t.Bottom));
                    if (ink && gapAt >= 0 && Math.Abs(x - gapAt) >= 3) { bounded = true; return (x + gapAt) / 2; }
                    if (!ink && gapAt < 0) gapAt = x;
                    if (ink) gapAt = -1;
                }
                x += dir;
            }
            return x;
        }
        int l = Side(-1, out bool lb), r = Side(1, out bool rb);
        if (!(lb && rb) && !boxed) return null;
        if (!lb) l = Math.Max(0, t.X - th / 3);
        if (!rb) r = Math.Min(w, t.Right + th / 3);
        if (r - l < t.Width || bot - top < t.Height) return null;
        return Rectangle.FromLTRB(l, top, r, bot);
    }
}
