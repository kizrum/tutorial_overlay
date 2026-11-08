using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>What the user marked while describing a problem: shapes (pencil strokes, circles, frames) as polylines in screen pixels, plus the frozen screen under them.</summary>
internal sealed class Sketch : IDisposable
{
    public readonly List<PointF[]> Strokes = new();
    public Bitmap? Shot;            // the virtual screen as it was when drawing started
    public Rectangle ShotRect;      // where Shot sits on the virtual screen

    public bool Empty => Strokes.Count == 0;

    public RectangleF Bounds()
    {
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var p in Strokes.SelectMany(s => s))
        {
            l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
        }
        return Empty ? RectangleF.Empty : RectangleF.FromLTRB(l, t, r, b);
    }

    /// <summary>The marked area with some context around it and the shapes drawn in, numbered (null: nothing marked).</summary>
    public Bitmap? Render()
    {
        if (Shot == null || Empty) return null;
        var b = Bounds();
        float pad = Math.Max(90, Math.Max(b.Width, b.Height) * 0.35f);
        var crop = Rectangle.Intersect(Rectangle.Round(RectangleF.FromLTRB(b.Left - pad, b.Top - pad, b.Right + pad, b.Bottom + pad)), ShotRect);
        if (crop.Width <= 0 || crop.Height <= 0) return null;
        var img = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(img))
        {
            g.DrawImage(Shot, new Rectangle(0, 0, crop.Width, crop.Height), new Rectangle(crop.X - ShotRect.X, crop.Y - ShotRect.Y, crop.Width, crop.Height), GraphicsUnit.Pixel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Native.ScaleAt(new PointF(b.X + b.Width / 2, b.Y + b.Height / 2));
            SketchPad.DrawShapes(g, Strokes, crop.X, crop.Y, k, crop.Size);   // numbered, as the user saw them
        }
        return img;
    }

    /// <summary>The marked area as a picture the agent can read (see <see cref="Render"/>), saved under help\.</summary>
    public string? SaveImage()
    {
        try
        {
            using var img = Render();
            if (img == null) return null;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorialOverlay", "help");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"sketch-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            img.Save(file, ImageFormat.Png);
            return file;
        }
        catch (Exception e)
        {
            Log.Warn("sketch image not saved: " + e.Message);
            return null;
        }
    }

    public void Dispose() { Shot?.Dispose(); Shot = null; }
}

/// <summary>
/// Drawing on a frozen screen: the user marks what they mean with a pencil, a circle or a frame — as many shapes as
/// they like, each numbered so the question can point at "2". The toolbar sits right above the step card (where the
/// user already looks, off the app's menus) and can be dragged by its grip.
/// Keys: 1 pencil, 2 circle, 3 frame, Ctrl+Z / Backspace undo the last shape, Delete clears, Enter or a right click
/// is done, Esc cancels (the previous drawing stays as it was).
/// </summary>
internal sealed class SketchPad : Form
{
    private enum Tool { Pen, Circle, Rect }

    private readonly Sketch _sketch;
    private readonly List<PointF[]> _strokes;
    private readonly Action<bool> _done;
    private readonly Bitmap _base;              // screen + veil, painted under the shapes
    private readonly Rectangle _vs;
    private readonly float _k;
    private readonly string _title;
    private readonly Rectangle _mon;            // the monitor the toolbar and the label are on (client px)
    private Tool _tool = Tool.Pen;
    private List<PointF>? _cur;                 // a pencil stroke being drawn (screen px)
    private PointF? _from;                      // a circle or frame being dragged out (screen px)
    private PointF _to;
    private RectangleF _preview;                // what the shape being drawn covered last time (client px)
    private RectangleF _bar, _grip, _label, _tip;
    private readonly List<(string id, RectangleF r)> _btns = new();
    private string _hover = "";
    private Point? _dragFrom;                   // dragging the toolbar by its grip
    private PointF _barFrom;
    private bool _finished;

    /// <summary>
    /// Draw on top of <paramref name="sketch"/>; <paramref name="done"/>(true) when the user keeps the result.
    /// <paramref name="title"/> replaces the default instruction (the agent's own request in ask_area).
    /// </summary>
    public static SketchPad Open(Sketch sketch, Action<bool> done, string? title = null)
    {
        var vs = Native.VirtualScreen();
        sketch.Shot?.Dispose();
        sketch.Shot = ScreenCapture.Capture(vs);
        sketch.ShotRect = vs;
        var pad = new SketchPad(sketch, vs, done, title);
        pad.Show();
        pad.Activate();
        return pad;
    }

    /// <summary>Close without keeping anything (timeout, superseded request).</summary>
    public void Cancel() => Finish(false);

    private SketchPad(Sketch sketch, Rectangle vs, Action<bool> done, string? title)
    {
        _sketch = sketch;
        _title = title ?? Settings.Current.Text.SketchTitle;
        _strokes = sketch.Strokes.ToList();
        _done = done;
        _vs = vs;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Palette.Ink;
        Text = "TutorialOverlay sketch";
        Cursor = Cursors.Cross;
        Bounds = vs;

        _base = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_base))
        {
            g.DrawImage(sketch.Shot!, 0, 0, vs.Width, vs.Height);
            using var veil = new SolidBrush(Color.FromArgb(96, 6, 8, 14));
            g.FillRectangle(veil, 0, 0, vs.Width, vs.Height);
        }

        // the toolbar goes right above the step card; without a card, to the corner where a card would be
        var card = Engine.CardRect is Rectangle cr && cr.Width > 0 ? cr : (Rectangle?)null;
        Native.GetCursorPos(out var cp);
        var at = card is Rectangle c0 ? new PointF(c0.X + c0.Width / 2f, c0.Y + c0.Height / 2f) : new PointF(cp.X, cp.Y);
        _k = Native.ScaleAt(at);
        var mon = Native.MonitorRectAt(at);
        _mon = new Rectangle(mon.X - vs.X, mon.Y - vs.Y, mon.Width, mon.Height);
        LayoutBar(PointF.Empty);
        float w = _bar.Width, h = _bar.Height, gap = 10 * _k;
        PointF pos;
        if (card is Rectangle c)
        {
            float x = c.Right - vs.X - w, y = c.Top - vs.Y - gap - h;
            if (y < _mon.Top + gap) y = c.Bottom - vs.Y + gap;          // a card at the top: under it
            pos = new PointF(x, y);
        }
        else pos = new PointF(_mon.Right - w - 24 * _k, _mon.Bottom - h - 24 * _k);
        LayoutBar(Clamp(pos));
        LayoutLabel();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // the pad is part of the conversation with the agent, not of the app — keep it out of captures
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    // ------------------------------------------------------------------ shapes

    public static void DrawStroke(Graphics g, PointF[] pts, Color c, float k)
    {
        if (pts.Length == 0) return;
        if (pts.Length == 1) pts = new[] { pts[0], new PointF(pts[0].X + 0.5f, pts[0].Y) };
        using (var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 6.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(shadow, pts);
        using var pen = new Pen(c, 4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    /// <summary>Where a shape's number goes: just outside its top right corner.</summary>
    public static PointF BadgeAt(PointF[] pts, float k)
    {
        float r = pts.Max(p => p.X), t = pts.Min(p => p.Y);
        return new PointF(r + 6 * k, t - 6 * k);
    }

    /// <summary>A shape's number in an accent disc, so a question can say "what is 2?".</summary>
    public static void DrawBadge(Graphics g, PointF c, int n, float k)
    {
        float rr = 11 * k;
        using (var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillEllipse(shadow, c.X - rr - 1.5f * k, c.Y - rr - 1.5f * k, 2 * rr + 3 * k, 2 * rr + 3 * k);
        using (var b = new SolidBrush(Palette.Accent)) g.FillEllipse(b, c.X - rr, c.Y - rr, 2 * rr, 2 * rr);
        using var tb = new SolidBrush(Palette.Ink);
        g.DrawString(n.ToString(), Theme.Font(12 * k, true), tb, new RectangleF(c.X - rr, c.Y - rr, 2 * rr, 2 * rr), Theme.Center);
    }

    /// <summary>
    /// All shapes with their numbers (screen points shifted by <paramref name="dx"/>, <paramref name="dy"/>); a number
    /// that would leave <paramref name="area"/> (a shape at the screen's edge) is kept inside it.
    /// </summary>
    public static void DrawShapes(Graphics g, IReadOnlyList<PointF[]> shapes, float dx, float dy, float k, SizeF area)
    {
        var local = shapes.Select(s => s.Select(p => new PointF(p.X - dx, p.Y - dy)).ToArray()).ToList();
        foreach (var s in local) DrawStroke(g, s, Palette.Accent, k);
        float m = 13 * k;
        for (int i = 0; i < local.Count; i++)
        {
            if (local[i].Length == 0) continue;
            var c = BadgeAt(local[i], k);
            DrawBadge(g, new PointF(Math.Clamp(c.X, m, Math.Max(m, area.Width - m)), Math.Clamp(c.Y, m, Math.Max(m, area.Height - m))), i + 1, k);
        }
    }

    private static PointF[] Ellipse(RectangleF r)
    {
        const int n = 64;
        var pts = new PointF[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double a = i * Math.PI * 2 / n;
            pts[i] = new PointF(r.X + r.Width / 2 + (float)Math.Cos(a) * r.Width / 2, r.Y + r.Height / 2 + (float)Math.Sin(a) * r.Height / 2);
        }
        return pts;
    }

    private static PointF[] Frame(RectangleF r) =>
        new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, r.Top) };

    /// <summary>The circle or frame being dragged out (Shift: a round circle, a square frame).</summary>
    private PointF[]? Dragged()
    {
        if (_from is not PointF a) return null;
        float dx = _to.X - a.X, dy = _to.Y - a.Y;
        if (Control.ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift))
        {
            float m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = Math.Sign(dx) * m; dy = Math.Sign(dy) * m;
        }
        var r = RectangleF.FromLTRB(Math.Min(a.X, a.X + dx), Math.Min(a.Y, a.Y + dy), Math.Max(a.X, a.X + dx), Math.Max(a.Y, a.Y + dy));
        return _tool == Tool.Circle ? Ellipse(r) : Frame(r);
    }

    private PointF[] Local(IEnumerable<PointF> pts) => pts.Select(p => new PointF(p.X - _vs.X, p.Y - _vs.Y)).ToArray();

    // ------------------------------------------------------------------ toolbar

    private string Tip(string id)
    {
        var loc = Settings.Current.Text;
        return id switch
        {
            "pen" => loc.SketchPen + " · 1",
            "circle" => loc.SketchCircle + " · 2",
            "rect" => loc.SketchRect + " · 3",
            "undo" => loc.SketchUndo + " · Ctrl+Z",
            "cancel" => loc.SketchCancelTip + " · Esc",
            "done" => loc.SketchDoneTip + " · Enter",
            "grip" => loc.SketchMove,
            _ => "",
        };
    }

    private float TextW(string t) => Theme.Measure(t, Theme.Font(13 * _k, true), 400 * _k).Width;

    /// <summary>Lays the toolbar out with its top left at <paramref name="at"/> (client px).</summary>
    private void LayoutBar(PointF at)
    {
        var loc = Settings.Current.Text;
        float k = _k, pad = 5 * k, b = 34 * k, h = b + 2 * pad, x = at.X + pad, y = at.Y + pad;
        _btns.Clear();
        _grip = new RectangleF(x, y, 14 * k, b);
        x += 14 * k + 2 * k;
        foreach (var id in new[] { "pen", "circle", "rect" }) { _btns.Add((id, new RectangleF(x, y, b, b))); x += b + 2 * k; }
        x += 9 * k;
        _btns.Add(("undo", new RectangleF(x, y, b, b))); x += b + 9 * k;
        float cw = TextW(loc.SketchCancel) + 40 * k;
        _btns.Add(("cancel", new RectangleF(x, y, cw, b))); x += cw + 6 * k;
        float dw = TextW(loc.Done) + 46 * k + (_strokes.Count > 0 ? TextW(_strokes.Count.ToString()) + 14 * k : 0);
        _btns.Add(("done", new RectangleF(x, y, dw, b))); x += dw;
        _bar = new RectangleF(at.X, at.Y, x + pad - at.X, h);
    }

    private PointF Clamp(PointF p) => new(
        Math.Clamp(p.X, _mon.Left + 8 * _k, Math.Max(_mon.Left + 8 * _k, _mon.Right - _bar.Width - 8 * _k)),
        Math.Clamp(p.Y, _mon.Top + 8 * _k, Math.Max(_mon.Top + 8 * _k, _mon.Bottom - _bar.Height - 8 * _k)));

    /// <summary>"Screen frozen · what to circle" at the bottom middle of the monitor.</summary>
    private void LayoutLabel()
    {
        var loc = Settings.Current.Text;
        float k = _k, maxW = Math.Min(620 * k, _mon.Width - 48 * k);
        var text = loc.SketchFrozen + " · " + _title;
        var sz = Theme.Measure(text, Theme.Font(13 * k, true), maxW - 40 * k);
        float w = sz.Width + 40 * k, h = Math.Max(30 * k, sz.Height + 14 * k);
        _label = new RectangleF(_mon.X + (_mon.Width - w) / 2, _mon.Bottom - h - 18 * k, w, h);
    }

    private void InvalidateBar()
    {
        var r = RectangleF.Union(_bar, _tip.IsEmpty ? _bar : _tip);
        Invalidate(Rectangle.Ceiling(RectangleF.Inflate(r, 4, 4)));
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.DrawImage(_base, clip, clip, GraphicsUnit.Pixel);
        Theme.Quality(g);
        float k = _k;
        DrawShapes(g, _strokes, _vs.X, _vs.Y, k, ClientSize);
        if (_cur != null) DrawStroke(g, Local(_cur), Palette.Accent, k);
        if (Dragged() is PointF[] d) DrawStroke(g, Local(d), Palette.Accent, k);

        // the whole screen is a picture now: an accent edge says so
        using (var edge = new Pen(Palette.Accent, 3 * k)) g.DrawRectangle(edge, 1.5f * k, 1.5f * k, ClientSize.Width - 3 * k, ClientSize.Height - 3 * k);
        DrawLabel(g);
        DrawBar(g);
    }

    private void DrawLabel(Graphics g)
    {
        float k = _k;
        var r = _label;
        using (var path = Theme.Rounded(r, r.Height / 2))
        {
            using (var b = new SolidBrush(Palette.Ink)) g.FillPath(b, path);
            using var pen = new Pen(Palette.Accent, 1 * k);
            g.DrawPath(pen, path);
        }
        using (var dot = new SolidBrush(Palette.Accent)) g.FillEllipse(dot, r.X + 14 * k, r.Y + r.Height / 2 - 4 * k, 8 * k, 8 * k);
        using var tb = new SolidBrush(Palette.Text);
        g.DrawString(Settings.Current.Text.SketchFrozen + " · " + _title, Theme.Font(13 * k, true), tb, new RectangleF(r.X + 30 * k, r.Y, r.Width - 40 * k, r.Height), new StringFormat(Theme.Center) { Alignment = StringAlignment.Near });
    }

    private void DrawBar(Graphics g)
    {
        var loc = Settings.Current.Text;
        float k = _k;
        using (var path = Theme.Rounded(_bar, 9 * k))
        {
            using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            using (var sp = Theme.Rounded(new RectangleF(_bar.X, _bar.Y + 4 * k, _bar.Width, _bar.Height), 9 * k)) g.FillPath(shadow, sp);
            using (var b = new SolidBrush(Palette.Ink)) g.FillPath(b, path);
            using var pen = new Pen(Palette.Line, 1 * k);
            g.DrawPath(pen, path);
        }
        // the grip: six dots
        var gc = _hover == "grip" ? Palette.Text : Palette.WithAlpha(Palette.TextDim, 0.7f);
        using (var gb = new SolidBrush(gc))
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 2; j++)
                    g.FillEllipse(gb, _grip.X + (4 + j * 5) * k, _grip.Y + _grip.Height / 2 + (i - 1) * 6 * k - 1.3f * k, 2.6f * k, 2.6f * k);
        // separators after the shapes and after undo
        using (var sep = new Pen(Palette.Line, 1 * k))
        {
            var rect = _btns.First(b => b.id == "rect").r;
            var undo = _btns.First(b => b.id == "undo").r;
            g.DrawLine(sep, rect.Right + 5.5f * k, _bar.Y + 12 * k, rect.Right + 5.5f * k, _bar.Bottom - 12 * k);
            g.DrawLine(sep, undo.Right + 4.5f * k, _bar.Y + 12 * k, undo.Right + 4.5f * k, _bar.Bottom - 12 * k);
        }
        foreach (var (id, r) in _btns)
        {
            bool hover = _hover == id;
            bool on = id == _tool switch { Tool.Pen => "pen", Tool.Circle => "circle", _ => "rect" };
            bool enabled = id != "undo" || _strokes.Count > 0;
            var c = new PointF(r.X + 17 * k, r.Y + r.Height / 2);
            switch (id)
            {
                case "pen" or "circle" or "rect" or "undo":
                {
                    var fg = Theme.OutlineButton(g, r, on ? "primary" : "ghost", hover, enabled, k);
                    if (on && !hover) fg = Palette.Accent;
                    var ic = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
                    if (id == "pen") Theme.IconPencil(g, ic, 17 * k, fg, 1.8f * k);
                    else if (id == "circle") using (var p = new Pen(fg, 1.8f * k)) g.DrawEllipse(p, ic.X - 7.5f * k, ic.Y - 6 * k, 15 * k, 12 * k);
                    else if (id == "rect") using (var p = new Pen(fg, 1.8f * k)) g.DrawRectangle(p, ic.X - 7 * k, ic.Y - 5.5f * k, 14 * k, 11 * k);
                    else Theme.IconUndo(g, ic, 18 * k, fg, 1.8f * k);
                    break;
                }
                case "cancel":
                {
                    var fg = Theme.OutlineButton(g, r, "ghost", hover, true, k);
                    Theme.IconCross(g, c, 11 * k, fg, 1.8f * k);
                    using var tb = new SolidBrush(fg);
                    g.DrawString(loc.SketchCancel, Theme.Font(13 * k, true), tb, new RectangleF(r.X + 30 * k, r.Y, r.Width - 32 * k, r.Height), new StringFormat(Theme.Center) { Alignment = StringAlignment.Near });
                    break;
                }
                case "done":
                {
                    var fg = Theme.OutlineButton(g, r, "primary", hover, true, k);
                    Theme.IconCheck(g, c, 13 * k, fg, 2.2f * k);
                    using (var tb = new SolidBrush(fg))
                        g.DrawString(loc.Done, Theme.Font(13 * k, true), tb, new RectangleF(r.X + 30 * k, r.Y, TextW(loc.Done) + 4 * k, r.Height), new StringFormat(Theme.Center) { Alignment = StringAlignment.Near });
                    if (_strokes.Count > 0)
                    {
                        // how many shapes go to the assistant
                        var n = _strokes.Count.ToString();
                        float nw = TextW(n) + 10 * k, nx = r.X + 30 * k + TextW(loc.Done) + 8 * k;
                        var pill = new RectangleF(nx, r.Y + r.Height / 2 - 9 * k, nw, 18 * k);
                        using (var pp = Theme.Rounded(pill, 9 * k))
                        using (var pb = new SolidBrush(Palette.Accent)) g.FillPath(pb, pp);
                        using var nb = new SolidBrush(Palette.Ink);
                        g.DrawString(n, Theme.Font(11.5f * k, true), nb, pill, Theme.Center);
                    }
                    break;
                }
            }
        }
        // what the hovered control does, and its key, above the toolbar
        _tip = RectangleF.Empty;
        var tipText = Tip(_hover);
        if (tipText.Length == 0) return;
        var f = Theme.Font(12 * k);
        var sz = Theme.Measure(tipText, f, 400 * k);
        var anchor = _hover == "grip" ? _grip : _btns.FirstOrDefault(b => b.id == _hover).r;
        float tw = sz.Width + 18 * k, th = sz.Height + 10 * k;
        float tx = Math.Clamp(anchor.X + anchor.Width / 2 - tw / 2, _mon.Left + 4 * k, _mon.Right - tw - 4 * k);
        float ty = _bar.Y - th - 6 * k;
        if (ty < _mon.Top) ty = _bar.Bottom + 6 * k;
        _tip = new RectangleF(tx, ty, tw, th);
        using (var path = Theme.Rounded(_tip, 6 * k))
        {
            using (var b = new SolidBrush(Palette.Ink)) g.FillPath(b, path);
            using var pen = new Pen(Palette.Line, 1 * k);
            g.DrawPath(pen, path);
        }
        using var tbr = new SolidBrush(Palette.Text);
        g.DrawString(tipText, f, tbr, _tip, Theme.Center);
    }

    // ------------------------------------------------------------------ input

    private PointF ScreenPt(Point client) => new(client.X + _vs.X, client.Y + _vs.Y);

    private string HitBar(Point p)
    {
        if (!_bar.Contains(p)) return "";
        if (_grip.Contains(p)) return "grip";
        foreach (var (id, r) in _btns) if (r.Contains(p)) return id;
        return "bar";
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { Finish(true); return; }
        if (e.Button != MouseButtons.Left) return;
        var hit = HitBar(e.Location);
        if (hit == "grip") { _dragFrom = Cursor.Position; _barFrom = _bar.Location; Capture = true; return; }
        if (hit.Length > 0) { Press(hit); return; }
        var p = ScreenPt(e.Location);
        if (_tool == Tool.Pen) _cur = new List<PointF> { p };
        else { _from = p; _to = p; _preview = RectangleF.Empty; }
        Capture = true;
    }

    private void Press(string id)
    {
        switch (id)
        {
            case "pen": SetTool(Tool.Pen); break;
            case "circle": SetTool(Tool.Circle); break;
            case "rect": SetTool(Tool.Rect); break;
            case "undo": Undo(); break;
            case "cancel": Finish(false); break;
            case "done": Finish(true); break;
        }
    }

    private void SetTool(Tool t)
    {
        _tool = t;
        InvalidateBar();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is Point from)
        {
            InvalidateBar();
            var cur = Cursor.Position;
            LayoutBar(Clamp(new PointF(_barFrom.X + cur.X - from.X, _barFrom.Y + cur.Y - from.Y)));
            InvalidateBar();
            return;
        }
        if (_cur != null)
        {
            var p = ScreenPt(e.Location);
            var last = _cur[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 2) return;
            _cur.Add(p);
            InvalidateShape(new[] { last, p });
            return;
        }
        if (_from != null)
        {
            _to = ScreenPt(e.Location);
            var now = Dragged()!;
            InvalidateRect(_preview);
            _preview = Box(Local(now));
            InvalidateRect(_preview);
            return;
        }
        var hv = HitBar(e.Location);
        if (hv == "bar") hv = "";
        if (hv != _hover) { _hover = hv; InvalidateBar(); }
        Cursor = hv == "grip" ? Cursors.SizeAll : _bar.Contains(e.Location) ? (hv.Length > 0 ? Cursors.Hand : Cursors.Default) : Cursors.Cross;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        Capture = false;
        if (_dragFrom != null) { _dragFrom = null; return; }
        if (_cur != null)
        {
            _strokes.Add(_cur.ToArray());
            _cur = null;
            Changed(_strokes[^1]);
            return;
        }
        if (_from != null)
        {
            _to = ScreenPt(e.Location);
            var shape = Dragged()!;
            _from = null;
            var box = Box(shape);
            // a click without a drag makes no shape
            if (box.Width >= 6 * _k || box.Height >= 6 * _k) { _strokes.Add(shape); Changed(shape); }
            else InvalidateRect(_preview);
        }
    }

    /// <summary>A shape was added or removed: repaint it, its number and the toolbar (whose count changes).</summary>
    private void Changed(PointF[] shape)
    {
        InvalidateShape(shape);
        InvalidateBar();
        LayoutBar(_bar.Location);
        InvalidateBar();
    }

    private static RectangleF Box(PointF[] pts) =>
        RectangleF.FromLTRB(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));

    private void InvalidateRect(RectangleF r)
    {
        if (r.IsEmpty && r.Width == 0) return;
        Invalidate(Rectangle.Ceiling(RectangleF.Inflate(r, 10 * _k, 10 * _k)));
    }

    private void InvalidateShape(IReadOnlyList<PointF> pts)
    {
        if (pts.Count == 0) return;
        var box = Box(Local(pts));
        // the number sits outside the top right corner
        InvalidateRect(RectangleF.FromLTRB(box.Left, box.Top - 24 * _k, box.Right + 24 * _k, box.Bottom));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case System.Windows.Forms.Keys.Escape: Finish(false); break;
            case System.Windows.Forms.Keys.Enter: Finish(true); break;
            case System.Windows.Forms.Keys.Delete: ClearAll(); break;
            case System.Windows.Forms.Keys.D1 or System.Windows.Forms.Keys.NumPad1: SetTool(Tool.Pen); break;
            case System.Windows.Forms.Keys.D2 or System.Windows.Forms.Keys.NumPad2: SetTool(Tool.Circle); break;
            case System.Windows.Forms.Keys.D3 or System.Windows.Forms.Keys.NumPad3: SetTool(Tool.Rect); break;
            case System.Windows.Forms.Keys.Back:
            case System.Windows.Forms.Keys.Z when e.Control: Undo(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    private void Undo()
    {
        if (_strokes.Count == 0) return;
        var last = _strokes[^1];
        _strokes.RemoveAt(_strokes.Count - 1);
        Changed(last);
    }

    private void ClearAll()
    {
        if (_strokes.Count == 0) return;
        _strokes.Clear();
        LayoutBar(_bar.Location);
        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // switched away (Alt+Tab, Win key): keep what was drawn rather than lose it
        if (Visible && !_finished) BeginInvoke(new Action(() => Finish(true)));
    }

    private void Finish(bool keep)
    {
        if (_finished) return;
        _finished = true;
        if (_cur != null) { _strokes.Add(_cur.ToArray()); _cur = null; }
        if (keep)
        {
            _sketch.Strokes.Clear();
            _sketch.Strokes.AddRange(_strokes);
        }
        Close();
        _done(keep);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _base.Dispose();
        base.Dispose(disposing);
    }
}
