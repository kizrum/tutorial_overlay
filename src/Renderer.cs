using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TutorialOverlay;

internal sealed class Ripple
{
    public PointF C;
    public float R0;
    public Color Color;
    public DateTime T0;
    public float Scale = 1;
    public const double DurationMs = 420;
}

/// <summary>Everything the canvas shows this frame.</summary>
internal sealed class Scene
{
    public readonly List<(Prim prim, float alpha)> Prims = new();
    public readonly List<HitArea> Holes = new();
    public readonly List<Ripple> Ripples = new();
    public float Dim;
    public float Pulse = 1;      // 0..1 breathing glow
    /// <summary>Pixel-probe points: left fully transparent so the probe sees the app, not our drawing.</summary>
    public readonly List<(PointF p, int r)> Probes = new();
    public float HoleScale = 1;
}

internal static class Renderer
{
    public static RectangleF ContentBounds(Scene sc)
    {
        RectangleF? all = null;
        foreach (var (p, _) in sc.Prims) all = all is RectangleF a ? RectangleF.Union(a, p.Extent()) : p.Extent();
        foreach (var r in sc.Ripples)
        {
            float rr = r.R0 + 34 * r.Scale;
            var e = new RectangleF(r.C.X - rr - 4, r.C.Y - rr - 4, 2 * rr + 8, 2 * rr + 8);
            all = all is RectangleF a ? RectangleF.Union(a, e) : e;
        }
        return all ?? RectangleF.Empty;
    }

    public static void Draw(Graphics g, Scene sc, RectangleF screen)
    {
        Theme.Quality(g);
        if (sc.Dim > 0.01f) DrawDim(g, sc, screen);
        foreach (var (p, a) in sc.Prims)
        {
            switch (p.Kind)
            {
                case PrimKind.Circle: Ring(g, p, a, sc.Pulse); break;
                case PrimKind.Rect: Frame(g, p, a, sc.Pulse); break;
                case PrimKind.Arrow: Arrow(g, p, a); break;
                case PrimKind.Badge: Badge(g, p, a); break;
                case PrimKind.Label: Label(g, p, a); break;
                case PrimKind.Info: Info(g, p, a); break;
                case PrimKind.Check: Check(g, p, a); break;
                case PrimKind.Checkbox: Checkbox(g, p, a, sc.Pulse); break;
            }
        }
        var now = DateTime.UtcNow;
        foreach (var r in sc.Ripples)
        {
            float t = (float)Math.Clamp((now - r.T0).TotalMilliseconds / Ripple.DurationMs, 0, 1);
            float ease = 1 - (1 - t) * (1 - t);
            float rr = r.R0 + 30 * r.Scale * ease;
            using var pen = new Pen(Palette.WithAlpha(r.Color, (1 - t) * 0.9f), Math.Max(1, 4 * r.Scale * (1 - t)));
            g.DrawEllipse(pen, r.C.X - rr, r.C.Y - rr, 2 * rr, 2 * rr);
        }
        if (sc.Probes.Count > 0)
        {
            var mode = g.CompositingMode;
            var smooth = g.SmoothingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.SmoothingMode = SmoothingMode.None;
            using var clear = new SolidBrush(Color.Transparent);
            foreach (var (p, r) in sc.Probes)
            {
                int half = r + 2;
                g.FillRectangle(clear, (float)Math.Round(p.X) - half, (float)Math.Round(p.Y) - half, 2 * half + 1, 2 * half + 1);
            }
            g.CompositingMode = mode;
            g.SmoothingMode = smooth;
        }
    }

    private static void DrawDim(Graphics g, Scene sc, RectangleF screen)
    {
        using var region = new Region(screen);
        foreach (var h in sc.Holes)
        {
            using var path = new GraphicsPath();
            float pad = 6 * sc.HoleScale;
            if (h.Circle) path.AddEllipse(h.C.X - h.R - pad, h.C.Y - h.R - pad, 2 * (h.R + pad), 2 * (h.R + pad));
            else { var r = h.Rect; r.Inflate(pad, pad); path.AddPath(Theme.Rounded(r, 8 * sc.HoleScale), false); }
            region.Exclude(path);
        }
        using var b = new SolidBrush(Color.FromArgb((int)(Math.Clamp(sc.Dim, 0, 0.92f) * 255), 6, 8, 14));
        g.FillRegion(b, region);
    }

    private static void Glow(Graphics g, Action<Pen> stroke, Color c, float width, float scale, float alpha, float pulse)
    {
        for (int i = 3; i >= 1; i--)
        {
            using var pen = new Pen(Palette.WithAlpha(c, alpha * (0.10f + 0.10f * pulse) * (4 - i) / 3f), width + i * 5 * scale) { LineJoin = LineJoin.Round };
            stroke(pen);
        }
        using (var shadow = new Pen(Color.FromArgb((int)(150 * alpha), 0, 0, 0), width + 2.5f * scale) { LineJoin = LineJoin.Round })
            stroke(shadow);
        using var main = new Pen(Palette.WithAlpha(c, alpha), width) { LineJoin = LineJoin.Round };
        stroke(main);
    }

    /// <summary>Thin style: a crisp line with a hairline shadow — no glow, no fill — for dense grids (piano roll).</summary>
    private static void Thin(Graphics g, Action<Pen> stroke, Color c, float width, float scale, float a)
    {
        using (var shadow = new Pen(Color.FromArgb((int)(170 * a), 0, 0, 0), width + 2f * scale)) stroke(shadow);
        using var main = new Pen(Palette.WithAlpha(c, a), width);
        stroke(main);
    }

    private static void Ring(Graphics g, Prim p, float a, float pulse)
    {
        var r = new RectangleF(p.A.X - p.R, p.A.Y - p.R, 2 * p.R, 2 * p.R);
        if (!p.Glow) { Thin(g, pen => g.DrawEllipse(pen, r), p.Color, p.Width, p.Scale, a); return; }
        using (var fill = new SolidBrush(Palette.WithAlpha(p.Color, a * 0.10f))) g.FillEllipse(fill, r);
        Glow(g, pen => g.DrawEllipse(pen, r), p.Color, p.Width, p.Scale, a, pulse);
    }

    private static void Frame(Graphics g, Prim p, float a, float pulse)
    {
        using var path = Theme.Rounded(p.Rect, p.Radius);
        if (!p.Glow) { Thin(g, pen => g.DrawPath(pen, path), p.Color, p.Width, p.Scale, a); return; }
        using (var fill = new SolidBrush(Palette.WithAlpha(p.Color, a * 0.07f))) g.FillPath(fill, path);
        Glow(g, pen => g.DrawPath(pen, path), p.Color, p.Width, p.Scale, a, pulse);
    }

    private static void Arrow(Graphics g, Prim p, float a)
    {
        float dx = p.B.X - p.A.X, dy = p.B.Y - p.A.Y;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 2) return;
        float ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
        float head = Math.Min(len * 0.45f, p.Width * 4.2f + 8 * p.Scale);
        float hw = head * 0.62f;
        var basePt = new PointF(p.B.X - ux * head, p.B.Y - uy * head);
        var headPts = new[]
        {
            p.B,
            new PointF(basePt.X + nx * hw, basePt.Y + ny * hw),
            new PointF(basePt.X + ux * head * 0.18f, basePt.Y + uy * head * 0.18f),
            new PointF(basePt.X - nx * hw, basePt.Y - ny * hw),
        };
        var shaftEnd = new PointF(basePt.X + ux * head * 0.25f, basePt.Y + uy * head * 0.25f);

        using (var shadow = new Pen(Color.FromArgb((int)(150 * a), 0, 0, 0), p.Width + 3 * p.Scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLine(shadow, p.A, shaftEnd);
            g.DrawPolygon(shadow, headPts);
        }
        using (var pen = new Pen(Palette.WithAlpha(p.Color, a), p.Width) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
            g.DrawLine(pen, p.A, shaftEnd);
        using var brush = new SolidBrush(Palette.WithAlpha(p.Color, a));
        g.FillPolygon(brush, headPts);
    }

    private static void Badge(Graphics g, Prim p, float a)
    {
        var r = new RectangleF(p.A.X - p.R, p.A.Y - p.R, 2 * p.R, 2 * p.R);
        var sh = r; sh.Offset(0, 1.5f * p.Scale); sh.Inflate(1.5f * p.Scale, 1.5f * p.Scale);
        using (var b = new SolidBrush(Color.FromArgb((int)(110 * a), 0, 0, 0))) g.FillEllipse(b, sh);
        using (var b = new LinearGradientBrush(r, Lighten(p.Color, 0.18f), p.Color, 90f))
        {
            b.SetSigmaBellShape(1f, 1f);
            g.FillEllipse(b, r);
        }
        using (var ring = new Pen(Color.FromArgb((int)(235 * a), 255, 255, 255), 2 * p.Scale)) g.DrawEllipse(ring, r);
        using var tb = new SolidBrush(Color.FromArgb((int)(255 * a), 255, 255, 255));
        var text = p.Text ?? "";
        float px = text.Length > 1 ? p.Size * 0.82f : p.Size;
        var tr = r; tr.Offset(0, 0.5f * p.Scale);
        g.DrawString(text, Theme.Font(px, true), tb, tr, Theme.Center);
    }

    private static void Panel(Graphics g, RectangleF r, float radius, float scale, float a)
    {
        var sh = r; sh.Offset(0, 3 * scale); sh.Inflate(2 * scale, 2 * scale);
        using (var shp = Theme.Rounded(sh, radius + 2 * scale))
        using (var b = new SolidBrush(Color.FromArgb((int)(80 * a), 0, 0, 0))) g.FillPath(b, shp);
        using var path = Theme.Rounded(r, radius);
        using (var b = new SolidBrush(Palette.WithAlpha(Palette.Ink, a * 0.94f))) g.FillPath(b, path);
        using var pen = new Pen(Palette.WithAlpha(Palette.Line, a), 1 * scale);
        g.DrawPath(pen, path);
    }

    private static void Label(Graphics g, Prim p, float a)
    {
        Panel(g, p.Rect, p.Radius, p.Scale, a);
        float bar = 4 * p.Scale;
        var barRect = new RectangleF(p.Rect.X + 5 * p.Scale, p.Rect.Y + 7 * p.Scale, bar, p.Rect.Height - 14 * p.Scale);
        using (var bp = Theme.Rounded(barRect, bar / 2))
        using (var b = new SolidBrush(Palette.WithAlpha(p.Color, a))) g.FillPath(b, bp);
        var text = new RectangleF(p.Rect.X + 12 * p.Scale + bar, p.Rect.Y + 8 * p.Scale, p.Rect.Width - 24 * p.Scale - bar + 2, p.Rect.Height - 16 * p.Scale + 2);
        using var tb = new SolidBrush(Palette.WithAlpha(Palette.Text, a));
        g.DrawString(p.Text, Theme.Font(p.Size), tb, text, Theme.Wrap);
    }

    private static void Info(Graphics g, Prim p, float a)
    {
        Panel(g, p.Rect, p.Radius, p.Scale, a);
        float s = p.Scale;
        // "i" icon: a filled disc with a drawn dot and stem
        var ic = new PointF(p.Rect.X + 12 * s + 11 * s, p.Rect.Y + 8 * s + 12 * s);
        float ir = 11 * s;
        using (var b = new SolidBrush(Palette.WithAlpha(p.Color, a))) g.FillEllipse(b, ic.X - ir, ic.Y - ir, 2 * ir, 2 * ir);
        using (var w = new SolidBrush(Palette.WithAlpha(Palette.Ink, a)))
        {
            g.FillEllipse(w, ic.X - 1.7f * s, ic.Y - 6.5f * s, 3.4f * s, 3.4f * s);
            using var stem = Theme.Rounded(new RectangleF(ic.X - 1.6f * s, ic.Y - 1.5f * s, 3.2f * s, 8.5f * s), 1.6f * s);
            g.FillPath(w, stem);
        }
        float x = p.Rect.X + 12 * s + 26 * s + 4 * s, y = p.Rect.Y + 8 * s;
        float wAvail = p.Rect.Right - x - 10 * s + 2;
        using var tb = new SolidBrush(Palette.WithAlpha(Palette.Text, a));
        if (p.Title != null)
        {
            var tf = Theme.Font(p.Size * 1.05f, true);
            var tsz = Theme.Measure(p.Title, tf, wAvail);
            g.DrawString(p.Title, tf, tb, new RectangleF(x, y, wAvail, tsz.Height + 2), Theme.Wrap);
            y += tsz.Height + 3 * s;
        }
        using var db = new SolidBrush(Palette.WithAlpha(p.Title != null ? Palette.TextDim : Palette.Text, a));
        g.DrawString(p.Text, Theme.Font(p.Size), db, new RectangleF(x, y, wAvail, p.Rect.Bottom - y), Theme.Wrap);
    }

    private static void Check(Graphics g, Prim p, float a)
    {
        var r = new RectangleF(p.A.X - p.R, p.A.Y - p.R, 2 * p.R, 2 * p.R);
        using (var b = new SolidBrush(Color.FromArgb((int)(110 * a), 0, 0, 0))) { var sh = r; sh.Inflate(1.5f * p.Scale, 1.5f * p.Scale); sh.Offset(0, 1.5f * p.Scale); g.FillEllipse(b, sh); }
        using (var b = new SolidBrush(Palette.WithAlpha(p.Color, a))) g.FillEllipse(b, r);
        Theme.IconCheck(g, p.A, p.R * 1.15f, Palette.WithAlpha(Palette.Ink, a), Math.Max(2, p.R * 0.24f));
    }

    private static void Checkbox(Graphics g, Prim p, float a, float pulse)
    {
        float s = p.Scale;
        using var path = Theme.Rounded(p.Rect, Math.Max(3 * s, p.Radius * 0.6f));
        if (p.On)
        {
            using (var b = new SolidBrush(Palette.WithAlpha(p.Color, a * 0.92f))) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb((int)(160 * a), 0, 0, 0), 2 * s)) g.DrawPath(pen, path);
            var c = new PointF(p.Rect.X + p.Rect.Width / 2, p.Rect.Y + p.Rect.Height / 2);
            float sz = Math.Min(p.Rect.Width, p.Rect.Height) * 0.62f;
            Theme.IconCheck(g, c, sz, Palette.WithAlpha(Palette.Ink, a), Math.Max(2, sz * 0.17f));
        }
        else
        {
            using (var b = new SolidBrush(Color.FromArgb((int)(70 * a), 0, 0, 0))) g.FillPath(b, path);
            Glow(g, pen => g.DrawPath(pen, path), p.Color, p.Width, s, a, pulse);
        }
    }

    private static Color Lighten(Color c, float k) => Color.FromArgb(c.A,
        (int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k));
}
