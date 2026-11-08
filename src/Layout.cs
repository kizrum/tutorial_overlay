using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace TutorialOverlay;

/// <summary>Anchor resolved to screen space for this frame.</summary>
internal sealed class AnchorGeo
{
    public RectangleF? Bounds;
    public PointF P;
    public float Scale = 1;
    public IntPtr Root;
    public int[] Pids = Array.Empty<int>();
    public RectangleF? View;          // visible area the point must be inside (scrollable panes)
    public bool FromLastKnown;        // the visual anchor is out of view; P is where it was last seen
    public string? GoneDir;           // which edge it most likely went past (up/down/left/right), when out of view
    public RectangleF? Found;         // a find anchor: the found text / template piece on screen
    public RectangleF? Visible;       // the visible thing itself (a template's icon, the caption at a spot): what "auto" frames
}

internal enum PrimKind { Circle, Rect, Arrow, Badge, Label, Info, Check, Checkbox, Stroke, Path, Wheel, Brackets, Pill, Marker, Band, Forbid, Hull, EdgeTab }

internal sealed class Prim
{
    public PrimKind Kind;
    public PointF A, B;          // circle/badge/check centre = A; arrow tail = A, tip = B
    public float R;
    public RectangleF Rect;      // rect/label/info/checkbox box; stroke bounds
    public PointF[]? Points;     // stroke / path polyline
    public float Progress;       // path: share already traced (drawn solid)
    public string? Text, Title;
    public Color Color;
    public float Width;          // stroke
    public float Size;           // font px
    public float Radius;         // corner radius
    public float Scale;
    public bool On;              // checkbox state
    public bool Auto;            // position chosen by us (may be moved to avoid overlaps)
    public bool AutoSize;        // radius chosen by us (may shrink next to neighbours)
    public bool Glow = true;
    public float Bend;           // arrow: how far its middle bows off the straight line, signed (+ to the left of tail→tip)
    public bool BendAuto;        // arrow: the side of the bow is ours to choose (Declutter may turn it to the freer side)
    public string End = "arrow"; // arrow: arrow | dot | chevron | bar | ring | none
    public string Taper = "none";// arrow: none | tail | both
    public string? Route;        // arrow: elbow-h (across first, then down/up) | elbow-v (down/up first) | null (straight or bowed)
    public bool Vertical;        // band: a column, not a row; edge tab: at a left/right edge
    public RectangleF? AimBox;   // curved arrow with a tip of its own choosing: what the tip stops just short of
    public float AimR;           // …a ring of this radius round the box's centre instead of the box itself (0: the box)
    public float AimGap;         // …and how far short

    /// <summary>
    /// Puts a curved arrow's tip where its last stretch points at the target's centre. The tip was first placed for a
    /// straight arrow; along a bow the arrow arrives at another angle and, left there, its head points past the
    /// target. A few rounds: the tip moves, the bow's control point with it.
    /// </summary>
    public void AimTip()
    {
        if (AimBox is not RectangleF box || Math.Abs(Bend) < 0.01f || Route != null) return;
        var c = new PointF(box.X + box.Width / 2, box.Y + box.Height / 2);
        for (int i = 0; i < 4; i++)
        {
            var ctrl = QuadControl(A, B, Bend);
            float dx = ctrl.X - c.X, dy = ctrl.Y - c.Y, l = (float)Math.Sqrt(dx * dx + dy * dy);
            if (l < 1) return;
            B = EdgePoint(c, dx / l, dy / l, box, AimR, AimGap);
        }
    }

    /// <summary>From <paramref name="c"/> along (ux, uy): where it leaves the box (or the ring <paramref name="r"/>), plus <paramref name="gap"/>.</summary>
    public static PointF EdgePoint(PointF c, float ux, float uy, RectangleF box, float r, float gap)
    {
        if (r > 0) return new PointF(c.X + ux * (r + gap), c.Y + uy * (r + gap));
        float hw = box.Width / 2 + gap, hh = box.Height / 2 + gap;
        float k = Math.Min(Math.Abs(ux) < 1e-3 ? float.MaxValue : hw / Math.Abs(ux), Math.Abs(uy) < 1e-3 ? float.MaxValue : hh / Math.Abs(uy));
        return new PointF(c.X + ux * k, c.Y + uy * k);
    }

    /// <summary>
    /// A quadratic curve from <paramref name="a"/> to <paramref name="b"/> whose middle lies <paramref name="bend"/> off
    /// the straight line (to the left of a→b when positive), as <paramref name="n"/>+1 points; a straight line when bend is 0.
    /// </summary>
    public static PointF[] Quad(PointF a, PointF b, float bend, int n)
    {
        if (Math.Abs(bend) < 0.01f) return new[] { a, b };
        var c = QuadControl(a, b, bend);
        var pts = new PointF[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            pts[i] = new PointF(u * u * a.X + 2 * u * t * c.X + t * t * b.X, u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y);
        }
        return pts;
    }

    /// <summary>The control point of <see cref="Quad"/>: twice the bow off the middle, so the curve's own middle is one bow off.</summary>
    public static PointF QuadControl(PointF a, PointF b, float bend)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Max(0.001f, (float)Math.Sqrt(dx * dx + dy * dy));
        float nx = dy / len, ny = -dx / len;     // left of a→b on screen (y grows down)
        return new PointF((a.X + b.X) / 2 + nx * 2 * bend, (a.Y + b.Y) / 2 + ny * 2 * bend);
    }

    /// <summary>An arrow's line from tail to tip: two points when straight, a curve when it bows.</summary>
    public PointF[] ArrowLine() => Route is "elbow-h" or "elbow-v" ? Elbow(A, B, Route == "elbow-h", 10 * Scale) : Quad(A, B, Bend, 24);

    /// <summary>
    /// Only across and up/down from <paramref name="a"/> to <paramref name="b"/> — across first when
    /// <paramref name="acrossFirst"/> — with the corner rounded (at most <paramref name="r"/>).
    /// </summary>
    public static PointF[] Elbow(PointF a, PointF b, bool acrossFirst, float r)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        if (Math.Abs(dx) < 1 || Math.Abs(dy) < 1) return new[] { a, b };
        r = Math.Min(r, Math.Min(Math.Abs(dx), Math.Abs(dy)) / 2);
        var corner = acrossFirst ? new PointF(b.X, a.Y) : new PointF(a.X, b.Y);
        float sx = Math.Sign(dx), sy = Math.Sign(dy);
        var p0 = acrossFirst ? new PointF(corner.X - sx * r, corner.Y) : new PointF(corner.X, corner.Y - sy * r);
        var p1 = acrossFirst ? new PointF(corner.X, corner.Y + sy * r) : new PointF(corner.X + sx * r, corner.Y);
        var pts = new List<PointF> { a, p0 };
        for (int i = 1; i < 6; i++)
        {
            float t = i / 6f, u = 1 - t;
            pts.Add(new PointF(u * u * p0.X + 2 * u * t * corner.X + t * t * p1.X, u * u * p0.Y + 2 * u * t * corner.Y + t * t * p1.Y));
        }
        pts.Add(p1);
        pts.Add(b);
        return pts.ToArray();
    }

    /// <summary>The tag above a forbidden area ("don't click"): an × and the text, on the area's top edge.</summary>
    public RectangleF ForbidTag()
    {
        float px = Size, sz = Theme.Measure(Text ?? "", Theme.Font(px), 400 * Scale).Width;
        float h = px + 10 * Scale, w = sz + 34 * Scale;
        return new RectangleF(Rect.X + 6 * Scale, Rect.Y - h / 2, w, h);
    }

    /// <summary>The caption beside an edge tab, on the screen's side of it.</summary>
    public RectangleF EdgeTabLabel()
    {
        float px = Size;
        var sz = Theme.Measure(Text ?? "", Theme.Font(px), 300 * Scale);
        float w = sz.Width + 20 * Scale, h = sz.Height + 10 * Scale;
        var c = new PointF(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
        float gap = 8 * Scale;
        // B points out of the screen: the caption goes the other way
        if (Math.Abs(B.X) > Math.Abs(B.Y))
            return new RectangleF(B.X > 0 ? Rect.Left - gap - w : Rect.Right + gap, c.Y - h / 2, w, h);
        return new RectangleF(c.X - w / 2, B.Y > 0 ? Rect.Top - gap - h : Rect.Bottom + gap, w, h);
    }

    /// <summary>The name of a group, on its outline's top edge.</summary>
    public RectangleF HullTag()
    {
        float px = Size, sz = Theme.Measure(Text ?? "", Theme.Font(px, true), 400 * Scale).Width;
        float h = px + 10 * Scale, w = sz + 20 * Scale;
        return new RectangleF(Rect.X + 12 * Scale, Rect.Y - h / 2, w, h);
    }

    /// <summary>The way an arrow's tail points, away from the tip (along the curve when it bows); unit length.</summary>
    public PointF TailDir()
    {
        var c = Math.Abs(Bend) < 0.01f ? B : QuadControl(A, B, Bend);
        float dx = A.X - c.X, dy = A.Y - c.Y, len = Math.Max(0.001f, (float)Math.Sqrt(dx * dx + dy * dy));
        return new PointF(dx / len, dy / len);
    }

    private static RectangleF BoxOf(PointF[] pts)
    {
        float l = pts.Min(p => p.X), t = pts.Min(p => p.Y), r = pts.Max(p => p.X), b = pts.Max(p => p.Y);
        return RectangleF.FromLTRB(l, t, r, b);
    }

    /// <summary>What the shape actually covers (no glow allowance) — used for "does it overlap?".</summary>
    public RectangleF Tight()
    {
        float g = Width / 2 + 1.5f * Scale;
        RectangleF r = Kind switch
        {
            PrimKind.Circle or PrimKind.Badge or PrimKind.Check => new RectangleF(A.X - R, A.Y - R, 2 * R, 2 * R),
            PrimKind.Arrow => BoxOf(ArrowLine()),
            _ => Rect,
        };
        r.Inflate(g, g);
        return r;
    }

    /// <summary>
    /// All a scroll-wheel hint draws: the wheel, its counter (±30) and the running arrows (up to ±45 either way, by
    /// direction) — the box for repainting it and for keeping the mascot and the card off it.
    /// </summary>
    public static RectangleF WheelBox(PointF c, float s) => new(c.X - 48 * s, c.Y - 48 * s, 96 * s, 96 * s);

    public RectangleF Extent()
    {
        float g = Width * 2 + 10 * Scale;
        RectangleF r = Kind switch
        {
            PrimKind.Circle or PrimKind.Badge or PrimKind.Check => new RectangleF(A.X - R, A.Y - R, 2 * R, 2 * R),
            PrimKind.Arrow => BoxOf(ArrowLine()),
            _ => Rect,
        };
        r.Inflate(g, g);
        if (Kind == PrimKind.Pill) r.Height += 8 * Scale;    // the dot under the capsule
        if (Kind == PrimKind.Forbid && Text is { Length: > 0 }) r = RectangleF.Union(r, RectangleF.Inflate(ForbidTag(), 4 * Scale, 4 * Scale));
        if (Kind == PrimKind.EdgeTab && Text is { Length: > 0 }) r = RectangleF.Union(r, RectangleF.Inflate(EdgeTabLabel(), 4 * Scale, 4 * Scale));
        if (Kind == PrimKind.Hull && Text is { Length: > 0 }) r = RectangleF.Union(r, RectangleF.Inflate(HullTag(), 4 * Scale, 4 * Scale));
        return r;
    }
}

/// <summary>A clickable button placed on screen (rendered as its own small window, not on the click-through canvas).</summary>
internal sealed class ButtonGeo
{
    public string Key = "";      // markId/shapeIndex
    public string MarkId = "";
    public string ButtonId = "";
    public string Text = "";
    public string Style = "primary";
    public string? Action;
    public Rectangle Rect;
    public float Scale;
    public Color Color;
    public bool Auto;
}

internal sealed class HitArea
{
    public bool Circle;
    public PointF C;
    public float R;
    public RectangleF Rect;

    public bool Contains(PointF p, float tol)
    {
        if (Circle)
        {
            float dx = p.X - C.X, dy = p.Y - C.Y;
            return dx * dx + dy * dy <= (R + tol) * (R + tol);
        }
        var r = Rect; r.Inflate(tol, tol);
        return r.Contains(p);
    }

    public float Distance(PointF p)
    {
        if (Circle) return Math.Max(0, (float)Math.Sqrt((p.X - C.X) * (p.X - C.X) + (p.Y - C.Y) * (p.Y - C.Y)) - R);
        float dx = Math.Max(Math.Max(Rect.Left - p.X, 0), p.X - Rect.Right);
        float dy = Math.Max(Math.Max(Rect.Top - p.Y, 0), p.Y - Rect.Bottom);
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }
}

internal sealed class MarkGeo
{
    public AnchorGeo A = new();
    public List<Prim> Prims = new();
    public List<Prim> DonePrims = new();   // shown instead of Prims when the mark is satisfied (checkbox / doneStyle=check)
    public List<HitArea> Hits = new();
    public List<ButtonGeo> Buttons = new();
    public RectangleF Bounds;
    public RectangleF TargetBounds;
    public PointF Center;
    public float Dim;                      // >0: mark asks for screen dimming with holes at targets
    public string Signature = "";
}

/// <summary>Turns a mark's shape specs into screen-space primitives for one frame.</summary>
internal static class Layout
{
    public static MarkGeo Build(Mark m, AnchorGeo ag, bool satisfied)
    {
        var geo = new MarkGeo { A = ag };
        float s = ag.Scale;
        var mon = Native.MonitorRectAt(ag.Bounds is RectangleF b0 ? new PointF(b0.X + b0.Width / 2, b0.Y + b0.Height / 2) : ag.P);
        RectangleF? targetBounds = null;
        Prim? firstTarget = null;
        bool hasCheckbox = m.Shapes.Any(x => x.Type == "checkbox");

        PointF Rel(float? x, float? y) => new(ag.P.X + (x ?? 0), ag.P.Y + (y ?? 0));
        // shapes without coordinates wrap what the anchor names: the element, or the text / template found in it (the
        // element is then only where it is searched for) — unless the anchor points at a spot (an offset)
        RectangleF? Wrap() => m.Anchor.HasPoint ? null : m.Anchor.Find != null ? ag.Found : ag.Bounds;
        Color ColorOf(ShapeSpec sp, Color fallback) => sp.Color ?? m.AutoColor ?? fallback;
        bool Thin(ShapeSpec sp) => sp.Style == "thin";
        float Stroke(ShapeSpec sp) => (sp.Width ?? (Thin(sp) ? 2f : 3.5f)) * s;

        void AddTargetBounds(RectangleF r) => targetBounds = targetBounds is RectangleF t ? RectangleF.Union(t, r) : r;

        // pass 1: shapes that define where the target is
        foreach (var sp in m.Shapes)
        {
            switch (sp.Type)
            {
                case "circle":
                {
                    PointF c; float r;
                    if (sp.X == null && sp.Y == null && Wrap() is RectangleF bb)
                    {
                        c = new PointF(bb.X + bb.Width / 2, bb.Y + bb.Height / 2);
                        r = sp.R ?? Math.Max(14 * s, Math.Max(bb.Width, bb.Height) / 2 + sp.Pad * s);
                    }
                    else { c = Rel(sp.X, sp.Y); r = sp.R ?? 22 * s; }
                    var p = new Prim { Kind = PrimKind.Circle, A = c, R = r, Color = ColorOf(sp, Palette.Accent), Width = Stroke(sp), Scale = s, AutoSize = sp.R == null, Glow = !Thin(sp) };
                    geo.Prims.Add(p);
                    if (sp.IsTarget) { geo.Hits.Add(new HitArea { Circle = true, C = c, R = r }); firstTarget ??= p; AddTargetBounds(new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r)); }
                    break;
                }
                case "auto":
                {
                    // the target at its own size: the visible thing when measured, else what the anchor names, else a
                    // small square at the spot - always a rectangle, so a tall caption is not ringed by a circle
                    var bb = ag.Visible ?? Wrap();
                    if (bb == null && m.Anchor.HasPoint && m.Anchor.Find != null)
                    {
                        // a spot counted from a found caption or icon (a place in Blender's 3D view): nothing is ever
                        // measured there, so it is a ring — a small square reads as a button that is not there. (A
                        // spot in an element is measured soon; its frame waits for that at a small size.)
                        float rr = 18 * s;
                        var ring = new Prim { Kind = PrimKind.Circle, A = ag.P, R = rr, Color = ColorOf(sp, Palette.Accent), Width = Stroke(sp), Scale = s, AutoSize = true, Glow = !Thin(sp) };
                        geo.Prims.Add(ring);
                        if (sp.IsTarget) { geo.Hits.Add(new HitArea { Circle = true, C = ag.P, R = rr }); firstTarget ??= ring; AddTargetBounds(new RectangleF(ag.P.X - rr, ag.P.Y - rr, 2 * rr, 2 * rr)); }
                        break;
                    }
                    RectangleF r = bb is RectangleF v ? v : new RectangleF(ag.P.X - 14 * s, ag.P.Y - 14 * s, 28 * s, 28 * s);
                    r.Inflate(sp.Pad * s, sp.Pad * s);
                    var p = new Prim
                    {
                        Kind = PrimKind.Rect, Rect = r, Color = ColorOf(sp, Palette.Accent), Width = Stroke(sp),
                        Radius = Math.Max(sp.Radius, 4) * s, Scale = s, Glow = !Thin(sp),
                    };
                    geo.Prims.Add(p);
                    if (sp.IsTarget) { geo.Hits.Add(new HitArea { Rect = r }); firstTarget ??= p; AddTargetBounds(r); }
                    break;
                }
                case "brackets":
                case "pill":
                case "marker":
                {
                    // at the target's own size, like "auto": corners round it, a capsule tight on it, a highlighter
                    // band under a text
                    RectangleF r;
                    if (sp.W != null && sp.H != null) { var tl = Rel(sp.X, sp.Y); r = new RectangleF(tl.X, tl.Y, sp.W.Value, sp.H.Value); }
                    else if ((ag.Visible ?? Wrap()) is RectangleF v) r = v;
                    else r = new RectangleF(ag.P.X - 14 * s, ag.P.Y - 14 * s, 28 * s, 28 * s);
                    float pad = (sp.PadGiven ? sp.Pad : sp.Type switch { "brackets" => 6, "pill" => 3, _ => 3 }) * s;
                    if (sp.Type == "marker") r.Inflate(pad, pad / 2);
                    else r.Inflate(pad, pad);
                    var kind = sp.Type switch { "brackets" => PrimKind.Brackets, "pill" => PrimKind.Pill, _ => PrimKind.Marker };
                    var p = new Prim
                    {
                        Kind = kind, Rect = r, Color = ColorOf(sp, Palette.Accent), Scale = s, Glow = !Thin(sp),
                        Width = (sp.Width ?? (sp.Type == "pill" ? 2.5f : Thin(sp) ? 2f : 3f)) * s,
                        Radius = (sp.Type == "pill" ? r.Height / 2 / s : Math.Min(sp.Radius, 3)) * s,
                    };
                    geo.Prims.Add(p);
                    if (sp.IsTarget) { geo.Hits.Add(new HitArea { Rect = r }); firstTarget ??= p; AddTargetBounds(r); }
                    break;
                }
                case "band":
                {
                    // a row (or a column, dir: "column") across the whole visible part of its pane
                    RectangleF r;
                    if (sp.W != null && sp.H != null) { var tl = Rel(sp.X, sp.Y); r = new RectangleF(tl.X, tl.Y, sp.W.Value, sp.H.Value); }
                    else if ((Wrap() ?? ag.Visible) is RectangleF v) r = v;
                    else r = new RectangleF(ag.P.X - 14 * s, ag.P.Y - 14 * s, 28 * s, 28 * s);
                    bool column = sp.FromDir is "column" or "col" or "vertical" or "v";
                    if (ag.View is RectangleF view && sp.W == null)
                        r = column ? RectangleF.FromLTRB(r.Left, view.Top, r.Right, view.Bottom) : RectangleF.FromLTRB(view.Left, r.Top, view.Right, r.Bottom);
                    float pad = (sp.PadGiven ? sp.Pad : 1) * s;
                    if (column) r.Inflate(pad, 0); else r.Inflate(0, pad);
                    var p = new Prim { Kind = PrimKind.Band, Rect = r, Vertical = column, Color = ColorOf(sp, Palette.Accent), Width = (sp.Width ?? 1.5f) * s, Scale = s };
                    geo.Prims.Add(p);
                    if (sp.IsTarget) { geo.Hits.Add(new HitArea { Rect = r }); firstTarget ??= p; AddTargetBounds(r); }
                    break;
                }
                case "forbid":
                {
                    // a place not to click: hatched in the warning colour, with a tag; never a target itself
                    RectangleF r;
                    if (sp.W != null && sp.H != null) { var tl = Rel(sp.X, sp.Y); r = new RectangleF(tl.X, tl.Y, sp.W.Value, sp.H.Value); }
                    else if ((Wrap() ?? ag.Visible) is RectangleF v) r = v;
                    else r = new RectangleF(ag.P.X - 14 * s, ag.P.Y - 14 * s, 28 * s, 28 * s);
                    r.Inflate((sp.PadGiven ? sp.Pad : 3) * s, (sp.PadGiven ? sp.Pad : 3) * s);
                    geo.Prims.Add(new Prim
                    {
                        Kind = PrimKind.Forbid, Rect = r, Color = sp.Color ?? ForbidColor, Width = (sp.Width ?? 2) * s, Scale = s,
                        Radius = Math.Min(sp.Radius, 8) * s, Text = sp.Text ?? Settings.Current.Text.ForbidTag, Size = (sp.Size ?? 12) * s,
                    });
                    if (sp.Target == true) { geo.Hits.Add(new HitArea { Rect = r }); AddTargetBounds(r); }
                    break;
                }
                case "rect":
                case "checkbox":
                {
                    RectangleF r;
                    if (sp.W != null && sp.H != null) { var tl = Rel(sp.X, sp.Y); r = new RectangleF(tl.X, tl.Y, sp.W.Value, sp.H.Value); }
                    else if (Wrap() is RectangleF bb && sp.Type == "rect")
                    {
                        r = bb; r.Offset(sp.X ?? 0, sp.Y ?? 0); r.Inflate(sp.Pad * s, sp.Pad * s);
                    }
                    else
                    {
                        float half = (sp.Type == "checkbox" ? (sp.R ?? 14) : 20) * s;
                        var c = sp.X == null && sp.Y == null && Wrap() is RectangleF cb ? new PointF(cb.X + cb.Width / 2, cb.Y + cb.Height / 2) : Rel(sp.X, sp.Y);
                        r = new RectangleF(c.X - half, c.Y - half, 2 * half, 2 * half);
                    }
                    var p = new Prim
                    {
                        Kind = sp.Type == "checkbox" ? PrimKind.Checkbox : PrimKind.Rect, Rect = r,
                        Color = ColorOf(sp, sp.Type == "checkbox" ? Palette.Success : Palette.Accent),
                        Width = Stroke(sp), Radius = (Thin(sp) ? Math.Min(sp.Radius, 3) : sp.Radius) * s, Scale = s, On = satisfied,
                        Glow = !Thin(sp),
                    };
                    geo.Prims.Add(p);
                    if (sp.Type == "checkbox") geo.DonePrims.Add(p);
                    if (sp.IsTarget || sp.Type == "checkbox") { geo.Hits.Add(new HitArea { Rect = r }); firstTarget ??= p; AddTargetBounds(r); }
                    break;
                }
            }
        }

        // pass 2: decorations positioned relative to the target
        var tb = targetBounds ?? ag.Bounds ?? new RectangleF(ag.P.X - 20 * s, ag.P.Y - 20 * s, 40 * s, 40 * s);
        var tc = new PointF(tb.X + tb.Width / 2, tb.Y + tb.Height / 2);
        int shapeIndex = -1;
        // arrows first, so an auto-placed caption can sit at the arrow's tail
        Prim? arrow = null;
        foreach (var sp in m.Shapes.Where(x => x.Type == "arrow"))
        {
            arrow = BuildArrow(sp, ag, firstTarget, tb, mon, s, ColorOf(sp, Palette.Accent), Stroke(sp) * 1.15f);
            geo.Prims.Add(arrow);
        }
        foreach (var sp in m.Shapes)
        {
            shapeIndex++;
            switch (sp.Type)
            {
                case "badge":
                {
                    // never bigger than the thing it numbers (dense grids: piano roll, step cells)
                    float r = sp.R ?? (firstTarget != null ? Math.Clamp(Math.Min(tb.Width, tb.Height) / 2, 8 * s, 13 * s) : 13 * s);
                    PointF c;
                    // outside the target (top-left), never on top of it; Declutter moves it if a neighbour is there
                    if (sp.X != null || sp.Y != null) c = Rel(sp.X, sp.Y);
                    else if (firstTarget?.Kind == PrimKind.Circle)
                    {
                        float d = firstTarget.R + r * 0.85f + 3 * s;
                        c = new PointF(firstTarget.A.X - d * 0.707f, firstTarget.A.Y - d * 0.707f);
                    }
                    else c = new PointF(tb.X - r * 0.75f - 2 * s, tb.Y - r * 0.75f - 2 * s);
                    var p = new Prim
                    {
                        Kind = PrimKind.Badge, A = c, R = r, Text = sp.Text ?? "1", Color = ColorOf(sp, Palette.Accent), Scale = s,
                        Size = (sp.Size ?? r / s * 1.05f) * s, Auto = sp.X == null && sp.Y == null,
                    };
                    geo.Prims.Add(p);
                    if (sp.IsTarget && firstTarget == null) { geo.Hits.Add(new HitArea { Circle = true, C = c, R = r }); AddTargetBounds(p.Extent()); }
                    break;
                }
                case "path":
                {
                    // a trajectory to follow: points relative to the anchor point, a curve through them
                    if (sp.Points is not { Count: >= 2 } pts) break;
                    var line = Engine.Densify(pts.Select(q => Rel(q.X, q.Y)).ToList(), sp.Smooth, 4);
                    float l = line.Min(q => q.X), tp = line.Min(q => q.Y), rr = line.Max(q => q.X), bt = line.Max(q => q.Y);
                    geo.Prims.Add(new Prim
                    {
                        Kind = PrimKind.Path, Points = line.ToArray(), Rect = RectangleF.FromLTRB(l - 14 * s, tp - 14 * s, rr + 14 * s, bt + 14 * s),
                        Color = ColorOf(sp, Palette.Accent), Width = Stroke(sp), Scale = s,
                    });
                    break;
                }
                case "check":
                {
                    float r = sp.R ?? 14 * s;
                    var c = sp.X != null || sp.Y != null ? Rel(sp.X, sp.Y) : tc;
                    geo.Prims.Add(new Prim { Kind = PrimKind.Check, A = c, R = r, Color = ColorOf(sp, Palette.Success), Width = Stroke(sp), Scale = s });
                    break;
                }
                case "label":
                case "info":
                {
                    bool info = sp.Type == "info";
                    float px = (sp.Size ?? (info ? 14.5f : 15f)) * s;
                    float maxW = (sp.W ?? (info ? 340 : 360)) * s;
                    var body = sp.Text ?? "";
                    string? title = info ? (sp.Title ?? null) : null;
                    var bodySz = Theme.Measure(body, Theme.Font(px), maxW);
                    var titleSz = title != null ? Theme.Measure(title, Theme.Font(px * 1.05f, true), maxW) : SizeF.Empty;
                    float padX = 12 * s, padY = 8 * s, icon = info ? 26 * s : 0, bar = info ? 0 : 4 * s;
                    float w = Math.Max(bodySz.Width, titleSz.Width) + padX * 2 + icon + bar;
                    float h = bodySz.Height + (title != null ? titleSz.Height + 3 * s : 0) + padY * 2;
                    if (info) h = Math.Max(h, 40 * s);
                    PointF tl;
                    if (sp.X != null || sp.Y != null) tl = Rel(sp.X, sp.Y);
                    else if (arrow != null)
                    {
                        // continue past the tail, along the arrow's direction (where a curved arrow leaves its tail)
                        var td = arrow.TailDir();
                        float ux = td.X, uy = td.Y, gap = 8 * s;
                        tl = new PointF(arrow.A.X + ux * (w / 2 + gap) - w / 2, arrow.A.Y + uy * (h / 2 + gap) - h / 2);
                    }
                    else
                    {
                        float gap = 12 * s;
                        tl = new PointF(tc.X - w / 2, tb.Bottom + gap);
                        if (tl.Y + h > mon.Bottom - 4 * s) tl.Y = tb.Top - gap - h;
                    }
                    tl.X = Math.Clamp(tl.X, mon.Left + 4 * s, Math.Max(mon.Left + 4 * s, mon.Right - w - 4 * s));
                    tl.Y = Math.Clamp(tl.Y, mon.Top + 4 * s, Math.Max(mon.Top + 4 * s, mon.Bottom - h - 4 * s));
                    geo.Prims.Add(new Prim
                    {
                        Kind = info ? PrimKind.Info : PrimKind.Label, Rect = new RectangleF(tl.X, tl.Y, w, h), Text = body, Title = title,
                        Size = px, Color = ColorOf(sp, info ? Palette.Named["blue"] : Palette.Accent), Scale = s, Radius = 8 * s,
                        Auto = sp.X == null && sp.Y == null,
                    });
                    break;
                }
                case "button":
                {
                    float px = (sp.Size ?? 14) * s;
                    var text = sp.Text ?? "OK";
                    var sz = Theme.Measure(text, Theme.Font(px, true), 600 * s);
                    float w = sp.W ?? (sz.Width + 30 * s), h = sp.H ?? (sz.Height + 16 * s);
                    var tl = sp.X != null || sp.Y != null ? Rel(sp.X, sp.Y) : new PointF(tc.X - w / 2, tb.Bottom + 12 * s);
                    geo.Buttons.Add(new ButtonGeo
                    {
                        Key = $"{m.Id}/{shapeIndex}", MarkId = m.Id, ButtonId = sp.ButtonId ?? m.Id, Text = text, Style = sp.Style ?? "primary",
                        Action = sp.Action, Rect = Rectangle.Round(new RectangleF(tl.X, tl.Y, w, h)), Scale = s, Color = ColorOf(sp, Palette.Accent),
                        Auto = sp.X == null && sp.Y == null,
                    });
                    break;
                }
                case "dim":
                    geo.Dim = Math.Max(geo.Dim, sp.Opacity);
                    break;
            }
        }

        // satisfied marks can leave a green tick behind instead of vanishing
        if (m.DoneStyle == "check" && !hasCheckbox)
            geo.DonePrims.Add(new Prim { Kind = PrimKind.Check, A = tc, R = Math.Clamp(Math.Min(tb.Width, tb.Height) / 2, 11 * s, 18 * s), Color = m.AutoColor ?? Palette.Success, Width = 3.5f * s, Scale = s });

        geo.TargetBounds = tb;
        geo.Center = geo.Hits.Count > 0 ? (geo.Hits[0].Circle ? geo.Hits[0].C : new PointF(geo.Hits[0].Rect.X + geo.Hits[0].Rect.Width / 2, geo.Hits[0].Rect.Y + geo.Hits[0].Rect.Height / 2)) : tc;
        RectangleF? all = null;
        foreach (var p in geo.Prims.Concat(geo.DonePrims)) all = all is RectangleF a ? RectangleF.Union(a, p.Extent()) : p.Extent();
        foreach (var bt in geo.Buttons) all = all is RectangleF a2 ? RectangleF.Union(a2, bt.Rect) : bt.Rect;
        geo.Bounds = all ?? tb;
        return geo;
    }

    /// <summary>The warning yellow a shade deeper, so the stripes of a forbidden area show on white too.</summary>
    internal static readonly Color ForbidColor = Color.FromArgb(255, 224, 168, 0);

    private static readonly Dictionary<string, (float x, float y)> Compass = new()
    {
        ["n"] = (0, -1), ["s"] = (0, 1), ["e"] = (1, 0), ["w"] = (-1, 0),
        ["ne"] = (0.707f, -0.707f), ["nw"] = (-0.707f, -0.707f), ["se"] = (0.707f, 0.707f), ["sw"] = (-0.707f, 0.707f),
        ["up"] = (0, -1), ["down"] = (0, 1), ["left"] = (-1, 0), ["right"] = (1, 0),
        ["top"] = (0, -1), ["bottom"] = (0, 1),
    };

    private static Prim BuildArrow(ShapeSpec sp, AnchorGeo ag, Prim? target, RectangleF tb, Rectangle mon, float s, Color color, float width)
    {
        float len = (sp.Length ?? 90) * s;
        // off a ring or a frame by a little; with no outline round the target, right up to the element
        float gap = (target != null ? 6 : 2) * s;
        var tc = new PointF(tb.X + tb.Width / 2, tb.Y + tb.Height / 2);
        PointF tail, tip;

        if (sp.From is PointF f && sp.To is PointF t)
        {
            tail = new PointF(ag.P.X + f.X, ag.P.Y + f.Y);
            tip = new PointF(ag.P.X + t.X, ag.P.Y + t.Y);
        }
        else
        {
            // direction the tail sits in, relative to the target
            (float ux, float uy) u;
            if (sp.FromDir != null && Compass.TryGetValue(sp.FromDir, out var cu)) u = cu;
            else
            {
                // default: the tail points toward the monitor centre so it stays on screen
                float mx = mon.X + mon.Width / 2f - tc.X, my = mon.Y + mon.Height / 2f - tc.Y;
                float ml = (float)Math.Sqrt(mx * mx + my * my);
                u = ml < 1 ? (-0.707f, -0.707f) : (mx / ml, my / ml);
            }
            if (sp.To is PointF t2) tip = new PointF(ag.P.X + t2.X, ag.P.Y + t2.Y);
            else if (target?.Kind == PrimKind.Circle) tip = Prim.EdgePoint(target.A, u.ux, u.uy, tb, target.R + target.Width, gap);
            else tip = Prim.EdgePoint(tc, u.ux, u.uy, tb, 0, gap);
            tail = sp.From is PointF f2 ? new PointF(ag.P.X + f2.X, ag.P.Y + f2.Y) : new PointF(tip.X + u.ux * len, tip.Y + u.uy * len);
        }
        var p = new Prim { Kind = PrimKind.Arrow, A = tail, B = tip, Color = color, Width = width, Scale = s, End = sp.End is string e && ShapeSpec.Ends.Contains(e) ? e : "arrow",
                         Taper = sp.Taper is string tp && ShapeSpec.Tapers.Contains(tp) ? tp : "none" };
        if (sp.Route == "elbow")
        {
            // across, then down (or down, then across): the longer way first, into the middle of the target's side
            bool across = Math.Abs(tail.X - tc.X) >= Math.Abs(tail.Y - tc.Y);
            // level with the target (or right above it): one straight leg into its side is the whole way
            bool level = across ? Math.Abs(tail.Y - tc.Y) < tb.Height / 2 : Math.Abs(tail.X - tc.X) < tb.Width / 2;
            if (sp.To == null)
            {
                if (level)
                    p.B = across ? new PointF(tail.X < tc.X ? tb.Left - gap : tb.Right + gap, tail.Y)
                                 : new PointF(tail.X, tail.Y < tc.Y ? tb.Top - gap : tb.Bottom + gap);
                else
                    p.B = across ? new PointF(tc.X, tail.Y < tc.Y ? tb.Top - gap : tb.Bottom + gap)
                                 : new PointF(tail.X < tc.X ? tb.Left - gap : tb.Right + gap, tc.Y);
            }
            if (!level || sp.To != null) p.Route = across ? "elbow-h" : "elbow-v";
        }
        else if (sp.Curve is float cv && cv > 0)
        {
            float dist = (float)Math.Sqrt((tip.X - tail.X) * (tip.X - tail.X) + (tip.Y - tail.Y) * (tip.Y - tail.Y));
            float bulge = Math.Min(Math.Clamp(cv * dist, 12 * s, 60 * s), dist * 0.5f);
            p.Bend = ChooseBend(tail, tip, bulge, mon, null, s);
            p.BendAuto = true;
            if (sp.To == null)
            {
                if (target?.Kind == PrimKind.Circle) { p.AimBox = new RectangleF(target.A.X - 1, target.A.Y - 1, 2, 2); p.AimR = target.R + target.Width; }
                else p.AimBox = tb;
                p.AimGap = gap;
                p.AimTip();
            }
        }
        return p;
    }

    /// <summary>
    /// Which way an arrow bows: the side whose curve stays on the monitor and runs over fewer of the
    /// <paramref name="obstacles"/> (other targets, captions); between equal sides, the one away from the screen's centre,
    /// so the arrow arcs around what is in the middle. Returns the signed bow (see <see cref="Prim.Quad"/>).
    /// </summary>
    public static float ChooseBend(PointF a, PointF b, float bulge, RectangleF mon, IReadOnlyList<RectangleF>? obstacles, float s)
    {
        var inner = RectangleF.Inflate(mon, -8 * s, -8 * s);
        var mc = new PointF(mon.X + mon.Width / 2, mon.Y + mon.Height / 2);
        float best = bulge;
        double bestScore = double.MaxValue;
        foreach (float sign in new[] { 1f, -1f })
        {
            var pts = Prim.Quad(a, b, sign * bulge, 16);
            double score = 0;
            if (pts.Length > 2 && !inner.Contains(pts[8])) score += 1000;
            if (obstacles != null && pts.Length > 2)
                for (int i = 2; i <= 14; i++)
                    foreach (var o in obstacles)
                        if (o.Width > 0 && o.Contains(pts[i])) score += 1;
            if (pts.Length > 2)
            {
                float dx = pts[8].X - mc.X, dy = pts[8].Y - mc.Y;
                score -= Math.Sqrt(dx * dx + dy * dy) * 1e-4;   // a tie-break only: far less than one obstacle
            }
            if (score < bestScore) { bestScore = score; best = sign * bulge; }
        }
        return best;
    }
}
