using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TutorialOverlay;

/// <summary>Our own colour palette. No bundled assets: every glyph and icon is drawn from primitives.</summary>
internal static class Palette
{
    public static readonly Color DefaultAccent = Color.FromArgb(255, 255, 77, 109);    // coral pink

    /// <summary>Current accent (config.json / tutorial "accent" can change it).</summary>
    public static Color Accent { get; set; } = DefaultAccent;
    public static readonly Color Ink = Color.FromArgb(255, 18, 20, 26);         // card / label background
    public static readonly Color InkRaised = Color.FromArgb(255, 32, 35, 44);
    public static readonly Color InkHover = Color.FromArgb(255, 46, 50, 62);
    public static readonly Color Line = Color.FromArgb(255, 58, 62, 76);
    public static readonly Color Text = Color.FromArgb(255, 240, 242, 247);
    public static readonly Color TextDim = Color.FromArgb(255, 156, 162, 178);
    public static readonly Color Success = Color.FromArgb(255, 46, 230, 166);
    public static readonly Color Warn = Color.FromArgb(255, 255, 196, 61);

    public static readonly Dictionary<string, Color> Named = new()
    {
        ["pink"] = DefaultAccent,
        ["red"] = Color.FromArgb(255, 255, 77, 79),
        ["orange"] = Color.FromArgb(255, 255, 159, 28),
        ["yellow"] = Color.FromArgb(255, 255, 210, 63),
        ["green"] = Success,
        ["teal"] = Color.FromArgb(255, 32, 201, 201),
        ["blue"] = Color.FromArgb(255, 61, 169, 252),
        ["purple"] = Color.FromArgb(255, 155, 93, 229),
        ["white"] = Color.FromArgb(255, 250, 250, 252),
    };

    public static Color WithAlpha(Color c, float a) => Color.FromArgb(Math.Clamp((int)(c.A * a), 0, 255), c.R, c.G, c.B);
}

/// <summary>Chooses a mark colour that stands out from the app's own colours around the target.</summary>
internal static class Contrast
{
    private static readonly Color[] Candidates =
    {
        Color.FromArgb(255, 255, 43, 214),   // magenta
        Color.FromArgb(255, 255, 122, 0),    // orange
        Color.FromArgb(255, 255, 225, 77),   // yellow
        Color.FromArgb(255, 0, 217, 255),    // cyan
        Color.FromArgb(255, 255, 59, 92),    // red-pink
        Color.FromArgb(255, 166, 107, 255),  // violet
    };

    /// <summary>"Redmean" perceptual distance between two colours (0..~765).</summary>
    public static double Distance(Color a, Color b)
    {
        double rm = (a.R + b.R) / 2.0, dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt((2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db);
    }

    /// <summary>The candidate whose closest background sample is furthest away.</summary>
    public static Color Pick(IReadOnlyList<Color> background)
    {
        if (background.Count == 0) return Palette.Accent;
        Color best = Candidates[0];
        double bestScore = -1;
        foreach (var c in Candidates)
        {
            double worst = double.MaxValue;
            foreach (var b in background) worst = Math.Min(worst, Distance(c, b));
            if (worst > bestScore) { bestScore = worst; best = c; }
        }
        return best;
    }
}

internal static class Theme
{
    private static readonly Dictionary<(int, bool), Font> Fonts = new();
    private static readonly Bitmap MeasureBmp = new(1, 1);
    private static readonly Graphics MeasureG = MakeMeasure();
    private static FontFamily? _family;

    /// <summary>The OS UI font family (used through the system, never shipped with the app).</summary>
    public static FontFamily Family => _family ??= SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;

    private static Graphics MakeMeasure()
    {
        var g = Graphics.FromImage(MeasureBmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    public static Font Font(float px, bool bold = false)
    {
        var key = ((int)Math.Round(px * 4), bold);
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(key, out var f))
                Fonts[key] = f = new Font(Family, Math.Max(6, px), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            return f;
        }
    }

    public static readonly StringFormat Wrap = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.LineLimit,
        Trimming = StringTrimming.EllipsisWord,
    };

    public static readonly StringFormat Center = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
    };

    public static SizeF Measure(string text, Font f, float maxWidth)
    {
        lock (MeasureG)
        {
            var sz = MeasureG.MeasureString(text, f, new SizeF(maxWidth, 10000), Wrap);
            return new SizeF((float)Math.Ceiling(sz.Width) + 1, (float)Math.Ceiling(sz.Height));
        }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Quality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // grayscale AA: ClearType is wrong on a per-pixel-alpha surface
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    // ---- vector icons (drawn, not loaded) ----

    public static void IconChevron(Graphics g, PointF c, float s, bool left, Color color, float w)
    {
        using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float dx = left ? -s * 0.25f : s * 0.25f;   // the middle vertex is the tip
        g.DrawLines(pen, new[] { new PointF(c.X - dx, c.Y - s * 0.45f), new PointF(c.X + dx, c.Y), new PointF(c.X - dx, c.Y + s * 0.45f) });
    }

    public static void IconCheck(Graphics g, PointF c, float s, Color color, float w)
    {
        using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(c.X - s * 0.45f, c.Y + s * 0.02f), new PointF(c.X - s * 0.12f, c.Y + s * 0.35f), new PointF(c.X + s * 0.48f, c.Y - s * 0.33f) });
    }

    public static void IconCross(Graphics g, PointF c, float s, Color color, float w)
    {
        using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float h = s * 0.38f;
        g.DrawLine(pen, c.X - h, c.Y - h, c.X + h, c.Y + h);
        g.DrawLine(pen, c.X - h, c.Y + h, c.X + h, c.Y - h);
    }

    public static void IconUndo(Graphics g, PointF c, float s, Color color, float w)
    {
        using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float r = s * 0.38f;
        g.DrawArc(pen, c.X - r, c.Y - r, 2 * r, 2 * r, 200, 250);
        // arrow head at the start of the arc (left side)
        var tip = new PointF(c.X - r * 0.94f, c.Y - r * 0.34f);
        g.DrawLines(pen, new[] { new PointF(tip.X - s * 0.04f, tip.Y - s * 0.30f), tip, new PointF(tip.X + s * 0.28f, tip.Y - s * 0.06f) });
    }

    public static void IconQuestion(Graphics g, PointF c, float s, Color color, float w)
    {
        using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float r = s * 0.26f;
        // hook of the "?" : an arc, then a short stem down to the middle
        g.DrawArc(pen, c.X - r, c.Y - s * 0.42f, 2 * r, 2 * r, 180, 270);
        g.DrawLine(pen, c.X, c.Y - s * 0.42f + 2 * r, c.X, c.Y + s * 0.12f);
        using var b = new SolidBrush(color);
        float d = w * 1.25f;
        g.FillEllipse(b, c.X - d / 2, c.Y + s * 0.32f - d / 2, d, d);
    }

    public static void IconBulb(Graphics g, PointF c, float s, Color color, float w)
    {
        using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float r = s * 0.30f;
        g.DrawArc(pen, c.X - r, c.Y - s * 0.46f, 2 * r, 2 * r, 140, 260);
        g.DrawLine(pen, c.X - r * 0.55f, c.Y + s * 0.10f, c.X + r * 0.55f, c.Y + s * 0.10f);
        g.DrawLine(pen, c.X - r * 0.40f, c.Y + s * 0.28f, c.X + r * 0.40f, c.Y + s * 0.28f);
    }

    public static void IconSkip(Graphics g, PointF c, float s, Color color, float w)
    {
        IconChevron(g, new PointF(c.X - s * 0.16f, c.Y), s, false, color, w);
        IconChevron(g, new PointF(c.X + s * 0.20f, c.Y), s, false, color, w);
    }
}
