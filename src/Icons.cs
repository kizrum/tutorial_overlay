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
}
