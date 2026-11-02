using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>Base for our clickable windows: topmost, no taskbar entry, never steals focus, hidden from capture.</summary>
internal class NoActivateForm : Form
{
    public NoActivateForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Ink;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = Native.WS_POPUP;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        int round = 2; // DWMWCP_ROUND (Windows 11)
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    public void ShowQuiet()
    {
        if (!Visible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void MoveTo(Rectangle r)
    {
        if (Bounds != r) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE);
    }

    protected static void Pill(Graphics g, RectangleF r, string style, bool hover, bool enabled, Color accent, float s)
    {
        using var path = Theme.Rounded(r, r.Height / 2);
        Color fill = style switch
        {
            "primary" => accent,
            "success" => Palette.Success,
            "danger" => Color.FromArgb(255, 96, 36, 44),
            "ghost" => Color.Transparent,
            _ => Palette.InkRaised,
        };
        if (hover && enabled) fill = style is "primary" or "success" ? Lighten(fill, 0.12f) : (style == "ghost" ? Palette.InkRaised : Palette.InkHover);
        if (!enabled) fill = Palette.WithAlpha(fill, 0.45f);
        if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (style is "secondary" or "ghost" or "danger")
            using (var pen = new Pen(Palette.WithAlpha(Palette.Line, enabled ? 1 : 0.5f), 1 * s)) g.DrawPath(pen, path);
    }

    protected static Color Lighten(Color c, float k) => Color.FromArgb(c.A,
        (int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k));

    protected static Color TextOn(string style) => style is "primary" or "success" ? Palette.Ink : Palette.Text;
}

/// <summary>
/// The step card: n/N, title, text, checklist, Back / Undo / Skip / Done / Stop and the agent's own buttons.
/// It positions itself away from every target and label, and the user can drag it (then it stays put).
/// </summary>
internal sealed class CardWindow : NoActivateForm
{
    private sealed class Btn
    {
        public string Id = "", Text = "", Style = "secondary";
        public string? Action;
        public bool Custom, Enabled = true;
        public RectangleF Rect;
        public string Icon = "";
    }

    private CardModel _model = new();
    private string _sig = "";
    private float _s = 1;
    private readonly List<Btn> _buttons = new();
    private Btn? _hover, _pressed;
    private RectangleF _closeRect;
    private bool _hoverClose;
    private Point? _dragFrom;
    private Point _dragOrigin;
    private bool _pinned;
    private int _pinnedStepVersion = -1;
    private bool _hotkeys;
    private Rectangle _target;
    private static readonly (int id, uint vk, string action)[] Hotkeys =
    {
        (1, 0x27, "done"), (2, 0x25, "back"), (3, 0x5A, "undo"), (4, 0x28, "skip"), (5, 0x58, "stop"),
    };

    public CardWindow() { Text = "TutorialOverlay step"; Size = new Size(10, 10); }

    public void UpdateModel(CardModel m)
    {
        if (!m.Visible)
        {
            if (Visible) Hide();
            SetHotkeys(false);
            return;
        }
        SetHotkeys(true);
        float s = Native.ScaleAt(m.Focus == default ? new PointF(Location.X, Location.Y) : m.Focus);
        var sig = Signature(m, s);
        _model = m;
        if (sig != _sig || Math.Abs(s - _s) > 0.01f)
        {
            _sig = sig;
            _s = s;
            var size = Measure();
            if (Size != size) Size = size;   // placement below decides the location
            Invalidate();
        }
        if (_pinned && _pinnedStepVersion != m.StepVersion && Intersects(Bounds, m.Avoid)) _pinned = false;
        if (!_pinned) Place(m);
        ShowQuiet();
    }

    private static string Signature(CardModel m, float s) =>
        string.Join("\u0001", m.State, m.Index, m.Total, m.Title, m.Text, m.Done, m.Required, m.Completed, m.CanBack, m.CanUndo,
            string.Join(",", m.Checklist.Select(c => c.label + c.done)), string.Join(",", m.Extra.Select(b => b.Id + b.Text)),
            string.Join(",", m.Controls), s);

    private void SetHotkeys(bool on)
    {
        if (on == _hotkeys || !IsHandleCreated) return;
        _hotkeys = on;
        foreach (var (id, vk, action) in Hotkeys)
        {
            if (on)
            {
                if (!Native.RegisterHotKey(Handle, id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, vk))
                    Log.Warn($"hotkey Ctrl+Alt+{vk:X2} ({action}) is taken by another app");
            }
            else Native.UnregisterHotKey(Handle, id);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            int hotkeyId = (int)m.WParam;
            var hk = Hotkeys.FirstOrDefault(h => h.id == hotkeyId);
            if (hk.action != null) Engine.UserAction(hk.action, "hotkey");
            return;
        }
        base.WndProc(ref m);
    }

    // ------------------------------------------------------------------ layout

    private float Pad => 18 * _s;
    private float ContentWidth => 372 * _s;

    private Size Measure()
    {
        var m = _model;
        float s = _s, y = Pad;
        y += 22 * s;                       // header row (step counter + close)
        y += 10 * s;                       // progress bar
        if (m.Title.Length > 0) y += Theme.Measure(m.Title, Theme.Font(18 * s, true), ContentWidth).Height + 6 * s;
        if (m.Text.Length > 0) y += Theme.Measure(m.Text, Theme.Font(14.5f * s), ContentWidth).Height + 6 * s;
        if (m.Checklist.Count > 0) y += 6 * s + m.Checklist.Count * 26 * s;
        else if (m.Required > 1) y += 22 * s;
        BuildButtons(ref y);
        y += 26 * s;                       // hint line
        return new Size((int)Math.Ceiling(ContentWidth + Pad * 2), (int)Math.Ceiling(y + Pad * 0.6f));
    }

    private void BuildButtons(ref float y)
    {
        var m = _model;
        float s = _s, h = 34 * s, gap = 8 * s, x0 = Pad, right = Pad + ContentWidth;
        _buttons.Clear();
        y += 12 * s;

        if (m.Extra.Count > 0)
        {
            float x = x0;
            foreach (var b in m.Extra)
            {
                float w = Theme.Measure(b.Text, Theme.Font(13.5f * s, true), 300 * s).Width + 28 * s;
                if (x + w > right && x > x0) { x = x0; y += h + gap; }
                _buttons.Add(new Btn { Id = b.Id, Text = b.Text, Style = b.Style, Action = b.Action, Custom = true, Rect = new RectangleF(x, y, Math.Min(w, ContentWidth), h) });
                x += w + gap;
            }
            y += h + gap;
        }

        if (m.State == "waiting") { y += 0; return; }
        float lx = x0;
        if (m.Controls.Contains("back"))
        {
            _buttons.Add(new Btn { Id = "back", Action = "back", Icon = "back", Text = "Назад", Enabled = m.CanBack, Rect = new RectangleF(lx, y, 96 * s, h) });
            lx += 96 * s + gap;
        }
        if (m.Controls.Contains("undo"))
        {
            _buttons.Add(new Btn { Id = "undo", Action = "undo", Icon = "undo", Style = "ghost", Enabled = m.CanUndo, Rect = new RectangleF(lx, y, h, h) });
        }
        float rx = right;
        if (m.Controls.Contains("done"))
        {
            float w = 116 * s;
            rx -= w;
            _buttons.Add(new Btn { Id = "done", Action = "done", Icon = "check", Text = m.Completed ? "Есть!" : "Готово", Style = m.Completed ? "success" : "primary", Enabled = !m.Completed, Rect = new RectangleF(rx, y, w, h) });
            rx -= gap;
        }
        if (m.Controls.Contains("skip"))
        {
            float w = 112 * s;
            rx -= w;
            _buttons.Add(new Btn { Id = "skip", Action = "skip", Icon = "skip", Text = "Пропустить", Style = "ghost", Enabled = !m.Completed, Rect = new RectangleF(rx, y, w, h) });
        }
        y += h;
    }

    private void Place(CardModel m)
    {
        float s = _s, margin = 16 * s;
        var work = Native.WorkAreaAt(m.Focus == default ? new PointF(0, 0) : m.Focus);
        var size = Size;
        var avoid = m.Avoid.Select(r => { r.Inflate(14 * s, 14 * s); return r; }).ToList();
        Rectangle At(float x, float y) => new((int)x, (int)y, size.Width, size.Height);
        var cands = new List<Rectangle>
        {
            At(work.Right - size.Width - margin, work.Bottom - size.Height - margin),
            At(work.Left + margin, work.Bottom - size.Height - margin),
            At(work.Right - size.Width - margin, work.Top + margin + 40 * s),
            At(work.Left + margin, work.Top + margin + 40 * s),
            At(work.Left + (work.Width - size.Width) / 2f, work.Bottom - size.Height - margin),
            At(work.Left + (work.Width - size.Width) / 2f, work.Top + margin + 40 * s),
            At(work.Right - size.Width - margin, work.Top + (work.Height - size.Height) / 2f),
            At(work.Left + margin, work.Top + (work.Height - size.Height) / 2f),
        };
        // stay where we are while that spot is still free (no jumping around)
        var current = new Rectangle(Location, size);
        if (_target != Rectangle.Empty && work.Contains(current) && !Intersects(current, avoid)) { MoveTo(current); return; }
        var best = cands.FirstOrDefault(c => !Intersects(c, avoid));
        if (best == Rectangle.Empty) best = cands.OrderBy(c => avoid.Sum(a => Overlap(c, a))).First();
        _target = best;
        MoveTo(best);
    }

    private static bool Intersects(RectangleF r, IEnumerable<RectangleF> rs) => rs.Any(a => a.IntersectsWith(r));
    private static float Overlap(RectangleF a, RectangleF b) { var i = RectangleF.Intersect(a, b); return i.Width * i.Height; }

    // ------------------------------------------------------------------ paint

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Quality(g);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;  // opaque surface: ClearType is fine
        var m = _model;
        float s = _s, w = ClientSize.Width, y = Pad;
        g.Clear(Palette.Ink);

        // top accent hairline
        using (var b = new LinearGradientBrush(new RectangleF(0, 0, w, 3 * s), Palette.Accent, Palette.Named["purple"], 0f))
            g.FillRectangle(b, 0, 0, w, 3 * s);
        using (var pen = new Pen(Palette.Line, 1)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        // header: STEP n / N and close
        var head = m.State == "waiting" ? "ОЖИДАНИЕ" : $"ШАГ {m.Index + 1} / {m.Total}";
        using (var b = new SolidBrush(Palette.Accent)) g.DrawString(head, Theme.Font(12 * s, true), b, Pad, y + 2 * s);
        if (m.Completed)
        {
            var hw = Theme.Measure(head, Theme.Font(12 * s, true), 300 * s).Width;
            var c = new PointF(Pad + hw + 14 * s, y + 10 * s);
            using (var b = new SolidBrush(Palette.Success)) g.FillEllipse(b, c.X - 8 * s, c.Y - 8 * s, 16 * s, 16 * s);
            Theme.IconCheck(g, c, 11 * s, Palette.Ink, 2.2f * s);
        }
        _closeRect = new RectangleF(w - Pad - 22 * s, y - 2 * s, 26 * s, 26 * s);
        if (m.Controls.Contains("stop") || m.State == "waiting")
        {
            if (_hoverClose) using (var b = new SolidBrush(Palette.InkHover)) g.FillEllipse(b, _closeRect);
            Theme.IconCross(g, new PointF(_closeRect.X + _closeRect.Width / 2, _closeRect.Y + _closeRect.Height / 2), 14 * s, _hoverClose ? Palette.Text : Palette.TextDim, 2 * s);
        }
        else _closeRect = RectangleF.Empty;
        y += 22 * s;

        // progress: one segment per step
        if (m.Total > 0)
        {
            float gap = 4 * s, segW = (ContentWidth - gap * (m.Total - 1)) / m.Total;
            if (segW < 4 * s) { segW = ContentWidth; gap = 0; }
            for (int i = 0; i < (segW == ContentWidth ? 1 : m.Total); i++)
            {
                var r = new RectangleF(Pad + i * (segW + gap), y + 2 * s, segW, 4 * s);
                float frac = segW == ContentWidth ? (m.Index + (m.Completed ? 1 : 0)) / (float)m.Total : 1;
                Color c = i < m.Index || (i == m.Index && m.Completed) ? Palette.Accent : i == m.Index ? Palette.WithAlpha(Palette.Accent, 0.45f) : Palette.InkRaised;
                using var path = Theme.Rounded(r, 2 * s);
                using (var b = new SolidBrush(segW == ContentWidth ? Palette.InkRaised : c)) g.FillPath(b, path);
                if (segW == ContentWidth)
                {
                    using var p2 = Theme.Rounded(new RectangleF(r.X, r.Y, r.Width * frac, r.Height), 2 * s);
                    using var b2 = new SolidBrush(Palette.Accent);
                    g.FillPath(b2, p2);
                }
            }
        }
        y += 10 * s;

        if (m.Title.Length > 0)
        {
            var f = Theme.Font(18 * s, true);
            var sz = Theme.Measure(m.Title, f, ContentWidth);
            using var b = new SolidBrush(Palette.Text);
            g.DrawString(m.Title, f, b, new RectangleF(Pad, y, ContentWidth, sz.Height + 2), Theme.Wrap);
            y += sz.Height + 6 * s;
        }
        if (m.Text.Length > 0)
        {
            var f = Theme.Font(14.5f * s);
            var sz = Theme.Measure(m.Text, f, ContentWidth);
            using var b = new SolidBrush(Palette.TextDim);
            g.DrawString(m.Text, f, b, new RectangleF(Pad, y, ContentWidth, sz.Height + 2), Theme.Wrap);
            y += sz.Height + 6 * s;
        }

        if (m.Checklist.Count > 0)
        {
            y += 6 * s;
            foreach (var (label, done) in m.Checklist)
            {
                var c = new PointF(Pad + 9 * s, y + 11 * s);
                if (done)
                {
                    using (var b = new SolidBrush(Palette.Success)) g.FillEllipse(b, c.X - 9 * s, c.Y - 9 * s, 18 * s, 18 * s);
                    Theme.IconCheck(g, c, 12 * s, Palette.Ink, 2.2f * s);
                }
                else using (var pen = new Pen(Palette.Line, 2 * s)) g.DrawEllipse(pen, c.X - 8 * s, c.Y - 8 * s, 16 * s, 16 * s);
                using var tb = new SolidBrush(done ? Palette.TextDim : Palette.Text);
                g.DrawString(label, Theme.Font(14 * s), tb, new RectangleF(Pad + 26 * s, y + 2 * s, ContentWidth - 26 * s, 22 * s), Theme.Wrap);
                if (done) using (var pen = new Pen(Palette.WithAlpha(Palette.TextDim, 0.7f), 1.2f * s))
                {
                    var tw = Math.Min(Theme.Measure(label, Theme.Font(14 * s), ContentWidth).Width, ContentWidth - 26 * s);
                    g.DrawLine(pen, Pad + 26 * s, y + 12.5f * s, Pad + 26 * s + tw, y + 12.5f * s);
                }
                y += 26 * s;
            }
        }
        else if (m.Required > 1)
        {
            using var b = new SolidBrush(Palette.TextDim);
            g.DrawString($"Отмечено {m.Done} из {m.Required}", Theme.Font(13 * s), b, Pad, y + 2 * s);
            y += 22 * s;
        }

        foreach (var bt in _buttons) DrawButton(g, bt);

        float hy = ClientSize.Height - Pad * 0.6f - 20 * s;
        using (var hb = new SolidBrush(Palette.WithAlpha(Palette.TextDim, 0.75f)))
            g.DrawString(m.State == "waiting" ? "Ctrl+Alt+X — завершить" : "Ctrl+Alt+→ готово · ← назад · ↓ пропустить · Z отменить",
                Theme.Font(11.5f * s), hb, new RectangleF(Pad, hy, ContentWidth, 18 * s), Theme.Wrap);
    }

    private void DrawButton(Graphics g, Btn b)
    {
        float s = _s;
        bool hover = _hover == b;
        Pill(g, b.Rect, b.Style, hover, b.Enabled, Palette.Accent, s);
        var fg = Palette.WithAlpha(TextOn(b.Style), b.Enabled ? 1 : 0.45f);
        var f = Theme.Font(13.5f * s, true);
        float iconW = b.Icon.Length > 0 ? 18 * s : 0;
        float textW = b.Text.Length > 0 ? Theme.Measure(b.Text, f, b.Rect.Width).Width : 0;
        float total = iconW + (textW > 0 && iconW > 0 ? 6 * s : 0) + textW;
        float x = b.Rect.X + (b.Rect.Width - total) / 2, cy = b.Rect.Y + b.Rect.Height / 2;
        bool iconAfter = b.Icon is "check" or "skip";
        float ix = iconAfter ? x + textW + 6 * s : x;
        float tx = iconAfter ? x : x + iconW + (iconW > 0 ? 6 * s : 0);
        var ic = new PointF(ix + iconW / 2, cy);
        switch (b.Icon)
        {
            case "back": Theme.IconChevron(g, ic, 13 * s, true, fg, 2.2f * s); break;
            case "undo": Theme.IconUndo(g, ic, 18 * s, fg, 2f * s); break;
            case "check": Theme.IconCheck(g, ic, 13 * s, fg, 2.4f * s); break;
            case "skip": Theme.IconSkip(g, ic, 11 * s, fg, 2f * s); break;
        }
        if (textW > 0)
            using (var tb = new SolidBrush(fg))
                g.DrawString(b.Text, f, tb, new RectangleF(tx, b.Rect.Y, textW + 2, b.Rect.Height), new StringFormat(Theme.Center) { Alignment = StringAlignment.Near });
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is Point from && e.Button == MouseButtons.Left)
        {
            var cur = Cursor.Position;
            MoveTo(new Rectangle(_dragOrigin.X + cur.X - from.X, _dragOrigin.Y + cur.Y - from.Y, Width, Height));
            _pinned = true;
            _pinnedStepVersion = _model.StepVersion;
            return;
        }
        var hover = _buttons.FirstOrDefault(b => b.Rect.Contains(e.Location));
        bool hc = _closeRect.Contains(e.Location);
        if (hover != _hover || hc != _hoverClose)
        {
            _hover = hover; _hoverClose = hc;
            Cursor = hover is { Enabled: true } || hc ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null || _hoverClose) { _hover = null; _hoverClose = false; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = _buttons.FirstOrDefault(b => b.Rect.Contains(e.Location) && b.Enabled);
        if (_pressed == null && !_closeRect.Contains(e.Location))
        {
            _dragFrom = Cursor.Position;
            _dragOrigin = Location;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragFrom = null;
        if (e.Button != MouseButtons.Left) return;
        if (_closeRect.Contains(e.Location)) { Engine.UserAction("stop", "card"); return; }
        var b = _buttons.FirstOrDefault(x => x.Rect.Contains(e.Location) && x.Enabled);
        if (b == null || b != _pressed) return;
        _pressed = null;
        if (b.Custom) Engine.ButtonPressed("card", b.Id, b.Action, "card");
        else Engine.UserAction(b.Action!, "card");
    }
}

/// <summary>A clickable pill button placed on screen by a "button" shape.</summary>
internal sealed class FloatButton : NoActivateForm
{
    public ButtonGeo Geo;
    private bool _hover;

    public FloatButton(ButtonGeo g) { Geo = g; Text = "TutorialOverlay button"; BackColor = Palette.Ink; }

    public void Apply(ButtonGeo g)
    {
        bool repaint = g.Text != Geo.Text || g.Style != Geo.Style || g.Color != Geo.Color;
        Geo = g;
        MoveTo(g.Rect);
        if (repaint) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Quality(g);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Palette.Ink);
        var r = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        var style = Geo.Style is "secondary" or "ghost" or "danger" or "success" ? Geo.Style : "primary";
        Pill(g, r, style, _hover, true, Geo.Color, Geo.Scale);
        using var tb = new SolidBrush(TextOn(style));
        g.DrawString(Geo.Text, Theme.Font(14 * Geo.Scale, true), tb, r, Theme.Center);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // pill-shaped window
        using var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), Height / 2f);
        Region = new Region(path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;
        using var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), Height / 2f);
        Region = new Region(path);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Cursor = Cursors.Hand; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            Engine.ButtonPressed(Geo.MarkId, Geo.ButtonId, Geo.Action, "button");
    }
}

/// <summary>Keeps one FloatButton window per visible "button" shape.</summary>
internal sealed class ButtonHost
{
    private readonly Dictionary<string, FloatButton> _windows = new();

    public void Update(List<ButtonGeo> buttons)
    {
        var keep = new HashSet<string>();
        foreach (var b in buttons)
        {
            keep.Add(b.Key);
            if (!_windows.TryGetValue(b.Key, out var w))
            {
                w = new FloatButton(b) { Bounds = b.Rect };
                _windows[b.Key] = w;
            }
            w.Apply(b);
            w.ShowQuiet();
        }
        foreach (var k in _windows.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _windows[k].Close();
            _windows[k].Dispose();
            _windows.Remove(k);
        }
    }
}
