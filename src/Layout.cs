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
}

internal enum PrimKind { Circle, Rect, Arrow, Badge, Label, Info, Check, Checkbox, Stroke, Path, Wheel }

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

    /// <summary>What the shape actually covers (no glow allowance) — used for "does it overlap?".</summary>
    public RectangleF Tight()
    {
        float g = Width / 2 + 1.5f * Scale;
        RectangleF r = Kind switch
        {
            PrimKind.Circle or PrimKind.Badge or PrimKind.Check => new RectangleF(A.X - R, A.Y - R, 2 * R, 2 * R),
            PrimKind.Arrow => RectangleF.FromLTRB(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Max(A.X, B.X), Math.Max(A.Y, B.Y)),
            _ => Rect,
        };
        r.Inflate(g, g);
        return r;
    }

    public RectangleF Extent()
    {
        float g = Width * 2 + 10 * Scale;
        RectangleF r = Kind switch
        {
            PrimKind.Circle or PrimKind.Badge or PrimKind.Check => new RectangleF(A.X - R, A.Y - R, 2 * R, 2 * R),
            PrimKind.Arrow => RectangleF.FromLTRB(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Max(A.X, B.X), Math.Max(A.Y, B.Y)),
            _ => Rect,
        };
        r.Inflate(g, g);
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
        // shapes without coordinates wrap the element — unless the anchor points at a spot inside it
        RectangleF? Wrap() => m.Anchor.HasPoint ? null : ag.Bounds;
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
                        // continue past the tail, along the arrow's direction
                        float dx = arrow.A.X - arrow.B.X, dy = arrow.A.Y - arrow.B.Y, len = Math.Max(1, (float)Math.Sqrt(dx * dx + dy * dy));
                        float ux = dx / len, uy = dy / len, gap = 8 * s;
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
        float gap = 6 * s;
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
            else if (target?.Kind == PrimKind.Circle) tip = new PointF(target.A.X + u.ux * (target.R + gap + target.Width), target.A.Y + u.uy * (target.R + gap + target.Width));
            else
            {
                float hw = tb.Width / 2 + gap, hh = tb.Height / 2 + gap;
                float k = Math.Min(Math.Abs(u.ux) < 1e-3 ? float.MaxValue : hw / Math.Abs(u.ux), Math.Abs(u.uy) < 1e-3 ? float.MaxValue : hh / Math.Abs(u.uy));
                tip = new PointF(tc.X + u.ux * k, tc.Y + u.uy * k);
            }
            tail = sp.From is PointF f2 ? new PointF(ag.P.X + f2.X, ag.P.Y + f2.Y) : new PointF(tip.X + u.ux * len, tip.Y + u.uy * len);
        }
        return new Prim { Kind = PrimKind.Arrow, A = tail, B = tip, Color = color, Width = width, Scale = s };
    }
}
