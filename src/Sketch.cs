using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>What the user circled with the pencil while describing a problem: strokes in screen pixels plus the frozen screen under them.</summary>
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

    /// <summary>The circled area with some context around it and the strokes drawn in — a picture the agent can read.</summary>
    public string? SaveImage()
    {
        if (Shot == null || Empty) return null;
        var b = Bounds();
        float pad = Math.Max(80, Math.Max(b.Width, b.Height) * 0.35f);
        var crop = Rectangle.Intersect(Rectangle.Round(RectangleF.FromLTRB(b.Left - pad, b.Top - pad, b.Right + pad, b.Bottom + pad)), ShotRect);
        if (crop.Width <= 0 || crop.Height <= 0) return null;
        try
        {
            using var img = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(img))
            {
                g.DrawImage(Shot, new Rectangle(0, 0, crop.Width, crop.Height), new Rectangle(crop.X - ShotRect.X, crop.Y - ShotRect.Y, crop.Width, crop.Height), GraphicsUnit.Pixel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float k = Native.ScaleAt(new PointF(b.X + b.Width / 2, b.Y + b.Height / 2));
                foreach (var s in Strokes) SketchPad.DrawStroke(g, s.Select(p => new PointF(p.X - crop.X, p.Y - crop.Y)).ToArray(), Palette.Accent, k);
            }
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
/// Pencil mode: a frozen picture of the screen with a light veil; the user circles the area they mean.
/// Left drag draws, Enter / right click / "Done" finishes, Ctrl+Z or Backspace removes the last stroke,
/// Delete clears, Esc leaves the previous drawing untouched.
/// </summary>
internal sealed class SketchPad : Form
{
    private readonly Sketch _sketch;
    private readonly List<PointF[]> _strokes;
    private readonly Action<bool> _done;
    private readonly Bitmap _base;              // screen + veil, painted under the strokes
    private readonly Rectangle _vs;
    private readonly float _k;
    private List<PointF>? _cur;
    private RectangleF _bar, _clearRect, _doneRect;
    private string _hover = "";
    private bool _finished;
    private readonly string _title;
    private float _titleH;

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
            using var veil = new SolidBrush(Color.FromArgb(70, 6, 8, 14));
            g.FillRectangle(veil, 0, 0, vs.Width, vs.Height);
        }

        // instructions at the top of the monitor the user is on
        Native.GetCursorPos(out var cp);
        var at = new PointF(cp.X, cp.Y);
        _k = Native.ScaleAt(at);
        var mon = Native.MonitorRectAt(at);
        float w = Math.Min(620 * _k, mon.Width - 32 * _k);
        // a long request from the agent wraps: the bar grows to fit it
        _titleH = Math.Max(22 * _k, Theme.Measure(_title, Theme.Font(14.5f * _k, true), w - 290 * _k).Height + 2 * _k);
        float h = 9 * _k + _titleH + 27 * _k;
        _bar = new RectangleF(mon.X - vs.X + (mon.Width - w) / 2, mon.Y - vs.Y + 18 * _k, w, h);
        float bh = 34 * _k, by = _bar.Y + (h - bh) / 2;
        _doneRect = new RectangleF(_bar.Right - 12 * _k - 110 * _k, by, 110 * _k, bh);
        _clearRect = new RectangleF(_doneRect.X - 8 * _k - 100 * _k, by, 100 * _k, bh);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // the pad is part of the conversation with the agent, not of the app — keep it out of captures
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public static void DrawStroke(Graphics g, PointF[] pts, Color c, float k)
    {
        if (pts.Length == 0) return;
        if (pts.Length == 1) pts = new[] { pts[0], new PointF(pts[0].X + 0.5f, pts[0].Y) };
        using (var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 6.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(shadow, pts);
        using var pen = new Pen(c, 4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    private PointF[] Local(IEnumerable<PointF> pts) => pts.Select(p => new PointF(p.X - _vs.X, p.Y - _vs.Y)).ToArray();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.DrawImage(_base, clip, clip, GraphicsUnit.Pixel);
        Theme.Quality(g);
        foreach (var s in _strokes) DrawStroke(g, Local(s), Palette.Accent, _k);
        if (_cur != null) DrawStroke(g, Local(_cur), Palette.Accent, _k);
        if (!clip.IntersectsWith(Rectangle.Ceiling(_bar))) return;

        var loc = Settings.Current.Text;
        float k = _k;
        using (var path = Theme.Rounded(_bar, 12 * k))
        {
            using (var b = new SolidBrush(Palette.Ink)) g.FillPath(b, path);
            using var pen = new Pen(Palette.Accent, 1.5f * k);
            g.DrawPath(pen, path);
        }
        Theme.IconPencil(g, new PointF(_bar.X + 28 * k, _bar.Y + _bar.Height / 2), 22 * k, Palette.Accent, 2.2f * k);
        float tx = _bar.X + 50 * k, tw = _clearRect.X - 10 * k - tx;
        using (var tb = new SolidBrush(Palette.Text))
            g.DrawString(_title, Theme.Font(14.5f * k, true), tb, new RectangleF(tx, _bar.Y + 9 * k, tw, _titleH), Theme.Wrap);
        using (var hb = new SolidBrush(Palette.TextDim))
            g.DrawString(loc.SketchText, Theme.Font(11.5f * k), hb, new RectangleF(tx, _bar.Y + 9 * k + _titleH, tw, 20 * k), Theme.Wrap);
        Pill(g, _clearRect, loc.SketchClear, false, _hover == "clear", _strokes.Count > 0);
        Pill(g, _doneRect, loc.Done, true, _hover == "done", true);
    }

    private void Pill(Graphics g, RectangleF r, string text, bool primary, bool hover, bool enabled)
    {
        using var path = Theme.Rounded(r, r.Height / 2);
        var fill = primary ? (hover ? Color.FromArgb(255, 255, 110, 135) : Palette.Accent) : (hover && enabled ? Palette.InkHover : Palette.InkRaised);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!primary) using (var pen = new Pen(Palette.Line, 1 * _k)) g.DrawPath(pen, path);
        using var tb = new SolidBrush(primary ? Palette.Ink : enabled ? Palette.Text : Palette.TextDim);
        g.DrawString(text, Theme.Font(13.5f * _k, true), tb, r, Theme.Center);
    }

    private void InvalidateBar() => Invalidate(Rectangle.Ceiling(RectangleF.Inflate(_bar, 2, 2)));

    private void InvalidateStroke(IReadOnlyList<PointF> pts)
    {
        if (pts.Count == 0) return;
        float l = pts.Min(p => p.X), t = pts.Min(p => p.Y), r = pts.Max(p => p.X), b = pts.Max(p => p.Y);
        var rect = RectangleF.FromLTRB(l - _vs.X, t - _vs.Y, r - _vs.X, b - _vs.Y);
        rect.Inflate(8 * _k, 8 * _k);
        Invalidate(Rectangle.Ceiling(rect));
    }

    private PointF ScreenPt(Point client) => new(client.X + _vs.X, client.Y + _vs.Y);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { Finish(true); return; }
        if (e.Button != MouseButtons.Left) return;
        if (_doneRect.Contains(e.Location)) { Finish(true); return; }
        if (_clearRect.Contains(e.Location)) { ClearAll(); return; }
        if (_bar.Contains(e.Location)) return;
        _cur = new List<PointF> { ScreenPt(e.Location) };
        Capture = true;
        InvalidateStroke(_cur);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_cur != null)
        {
            var p = ScreenPt(e.Location);
            var last = _cur[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 2) return;
            _cur.Add(p);
            InvalidateStroke(new[] { last, p });
            return;
        }
        var hv = _doneRect.Contains(e.Location) ? "done" : _clearRect.Contains(e.Location) ? "clear" : "";
        if (hv != _hover) { _hover = hv; InvalidateBar(); }
        Cursor = _bar.Contains(e.Location) ? (hv.Length > 0 ? Cursors.Hand : Cursors.Default) : Cursors.Cross;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_cur == null || e.Button != MouseButtons.Left) return;
        _strokes.Add(_cur.ToArray());
        _cur = null;
        Capture = false;
        InvalidateBar();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case System.Windows.Forms.Keys.Escape: Finish(false); break;
            case System.Windows.Forms.Keys.Enter: Finish(true); break;
            case System.Windows.Forms.Keys.Delete: ClearAll(); break;
            case System.Windows.Forms.Keys.Back:
            case System.Windows.Forms.Keys.Z when e.Control:
                if (_strokes.Count == 0) break;
                InvalidateStroke(_strokes[^1]);
                _strokes.RemoveAt(_strokes.Count - 1);
                InvalidateBar();
                break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    private void ClearAll()
    {
        if (_strokes.Count == 0) return;
        _strokes.Clear();
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
