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
        if (Settings.Global.HideFromCapture) Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
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
        public bool Tile;          // equal-width tile: icon on top, caption below
        public bool Status;        // shows a state ("Got it!"): drawn bright, but not clickable
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
    private bool _helpOpen;                 // "?" pressed: showing the two help choices
    private int _helpStepVersion = -1;
    private string _hotkeySig = "";
    private readonly Dictionary<int, string> _hotkeyIds = new();
    private Rectangle _target;

    public CardWindow() { Text = "TutorialOverlay step"; Size = new Size(10, 10); }

    public void UpdateModel(CardModel m)
    {
        if (!m.Visible)
        {
            Engine.CardRect = null;
            if (Visible) Hide();
            SetHotkeys(false);
            return;
        }
        SetHotkeys(true);
        if (_helpOpen && _helpStepVersion != m.StepVersion && m.Index != _model.Index) _helpOpen = false;
        float s = Native.ScaleAt(m.Focus == default ? new PointF(Location.X, Location.Y) : m.Focus);
        var sig = Signature(m, s);
        _model = m;
        sig += _helpOpen ? "|help" : "";
        if (sig != _sig || Math.Abs(s - _s) > 0.01f)
        {
            _sig = sig;
            _s = s;
            var size = Measure();
            if (Size != size) Size = size;   // placement below decides the location
            Invalidate();
        }
        if (Busy(m)) Invalidate();          // the spinner keeps turning while we wait for the assistant
        if (_pinned && _pinnedStepVersion != m.StepVersion && Intersects(Bounds, m.Avoid)) _pinned = false;
        if (!_pinned) Place(m);
        ShowQuiet();
        Engine.CardRect = Bounds;
    }

    private static string Signature(CardModel m, float s) =>
        string.Join("\u0001", Settings.Current.Lang, Settings.Current.Accent.ToArgb(), Settings.Current.HotkeySignature, m.State, m.Index, m.Total, m.Title, m.Text, m.Done, m.Required, m.Completed, m.CanBack, m.CanUndo,
            string.Join(",", m.Checklist.Select(c => c.label + c.done)), string.Join(",", m.Extra.Select(b => b.Id + b.Text)),
            string.Join(",", m.Controls), m.Why, m.Hint, m.HintTitle, m.Issue, m.HelpPending, m.Highlighting, m.Review, s);

    /// <summary>Global hotkeys exist only while the card is shown; re-registered when the settings change.</summary>
    private void SetHotkeys(bool on)
    {
        var settings = Settings.Current;
        var sig = on ? settings.HotkeySignature : "";
        if (sig == _hotkeySig || !IsHandleCreated) return;
        foreach (var id in _hotkeyIds.Keys) Native.UnregisterHotKey(Handle, id);
        _hotkeyIds.Clear();
        _hotkeySig = sig;
        if (!on) return;
        int next = 1;
        foreach (var (action, chord) in settings.Hotkeys)
        {
            int id = next++;
            if (Native.RegisterHotKey(Handle, id, chord.Mods | Native.MOD_NOREPEAT, chord.Vk)) _hotkeyIds[id] = action;
            else Log.Warn($"hotkey {chord} ({action}) is taken by another app");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            if (_hotkeyIds.TryGetValue((int)m.WParam, out var action)) Engine.UserAction(action, "hotkey");
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
        if (m.Why != null) y += WhyHeight(m.Why) + 8 * s;
        if (m.Checklist.Count > 0) y += 6 * s + m.Checklist.Count * 26 * s;
        else if (m.Required > 1) y += 22 * s;
        var note = Note(m);
        if (note != null) y += NoteHeight(note.Value) + 10 * s;
        BuildButtons(ref y);
        y += 8 * s + HintHeight();         // hint line (may wrap)
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
        var loc = Settings.Current.Text;
        if (_helpOpen)
        {
            // the two kinds of trouble, as big honest buttons
            float bw = (ContentWidth - gap) / 2;
            bool sideBySide = Math.Max(TextW(loc.CantFind), TextW(loc.DontGet)) + 28 * s <= bw;
            if (sideBySide)
            {
                _buttons.Add(new Btn { Id = "cant_find", Action = "cant_find", Text = loc.CantFind, Style = "primary", Rect = new RectangleF(x0, y, bw, h) });
                _buttons.Add(new Btn { Id = "dont_understand", Action = "dont_understand", Text = loc.DontGet, Style = "secondary", Rect = new RectangleF(x0 + bw + gap, y, bw, h) });
                y += h + gap;
            }
            else
            {
                _buttons.Add(new Btn { Id = "cant_find", Action = "cant_find", Text = loc.CantFind, Style = "primary", Rect = new RectangleF(x0, y, ContentWidth, h) });
                y += h + gap;
                _buttons.Add(new Btn { Id = "dont_understand", Action = "dont_understand", Text = loc.DontGet, Style = "secondary", Rect = new RectangleF(x0, y, ContentWidth, h) });
                y += h + gap;
            }
            float cw = 110 * s;
            _buttons.Add(new Btn { Id = "help_other", Action = "help_other", Text = loc.Other, Style = "secondary", Rect = new RectangleF(x0, y, ContentWidth - cw - gap, h) });
            _buttons.Add(new Btn { Id = "help_cancel", Action = "help_cancel", Text = loc.Cancel, Style = "ghost", Rect = new RectangleF(x0 + ContentWidth - cw, y, cw, h) });
            y += h;
            return;
        }
        // every secondary action is a same-size tile with an icon AND a caption, so nothing is a mystery glyph
        var tiles = new List<Btn>();
        if (m.Controls.Contains("back"))
            tiles.Add(new Btn { Id = "back", Action = "back", Icon = "back", Text = loc.Back, Enabled = m.CanBack, Tile = true });
        if (m.Controls.Contains("undo"))
            tiles.Add(new Btn { Id = "undo", Action = "undo", Icon = "undo", Text = loc.Undo, Enabled = m.CanUndo, Tile = true });
        if (m.Controls.Contains("skip"))
            tiles.Add(new Btn { Id = "skip", Action = "skip", Icon = "skip", Text = loc.Skip, Enabled = !m.Completed, Tile = true });
        if (m.Controls.Contains("help"))
            tiles.Add(new Btn { Id = "help", Action = "help_open", Icon = "help", Text = loc.Help, Style = m.HelpPending ? "active" : "secondary", Tile = true });
        if (tiles.Count > 0)
        {
            float th = 54 * s, tw = (ContentWidth - gap * (tiles.Count - 1)) / tiles.Count;
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].Rect = new RectangleF(x0 + i * (tw + gap), y, tw, th);
                _buttons.Add(tiles[i]);
            }
            y += th + gap;
        }
        if (m.Controls.Contains("done"))
        {
            float dh = 42 * s;
            _buttons.Add(new Btn
            {
                Id = "done", Action = "done", Icon = "check", Text = m.Completed ? loc.Got : loc.Done,
                Style = m.Completed ? "success" : "primary", Enabled = !m.Completed, Status = m.Completed, Rect = new RectangleF(x0, y, ContentWidth, dh),
            });
            y += dh;
        }
        else if (tiles.Count > 0) y -= gap;
    }

    private float TextW(string t) => Theme.Measure(t, Theme.Font(13.5f * _s, true), 600 * _s).Width;

    private float HintHeight() => Theme.Measure(Settings.Current.HintLine(_model.State == "waiting"), Theme.Font(11.5f * _s), ContentWidth).Height;

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

    private void Relayout()
    {
        _sig = "";            // next UpdateModel re-measures
        UpdateModel(_model);
    }

    /// <summary>One message block above the buttons: the agent's hint, else "highlighting / sent", else why targets are hidden.</summary>
    /// <summary>Waiting for the next steps or for an answer to "help": show motion, not a frozen card.</summary>
    private static bool Busy(CardModel m) => m.State == "waiting" || (m.HelpPending && m.Hint == null);

    private (string? title, string text, Color color)? Note(CardModel m)
    {
        var loc = Settings.Current.Text;
        if (m.State == "waiting") return (null, loc.Preparing, Palette.Named["blue"]);
        if (m.Hint != null) return (m.HintTitle, m.Hint, Palette.Named["blue"]);
        if (m.HelpPending) return (null, m.Highlighting ? loc.Highlighting + " " + loc.HelpSent : loc.HelpSent, Palette.Warn);
        if (m.Issue != null) return (null, m.Issue, Palette.Warn);
        if (m.Review) return (null, loc.Review, Palette.Named["blue"]);
        return null;
    }

    // "Why it works this way": a quiet block with a bulb, so the user learns the idea and not just the click
    private float WhyHeight(string why) =>
        Theme.Measure(why, Theme.Font(13.5f * _s), ContentWidth - 40 * _s).Height + Theme.Font(12 * _s, true).Height + 18 * _s;

    private void DrawWhy(Graphics g, string why, float y)
    {
        float s = _s, h = WhyHeight(why);
        var r = new RectangleF(Pad, y, ContentWidth, h);
        var gold = Palette.Named["yellow"];
        using (var path = Theme.Rounded(r, 8 * s))
        {
            using (var b = new SolidBrush(Palette.WithAlpha(gold, 0.07f))) g.FillPath(b, path);
            using var pen = new Pen(Palette.WithAlpha(gold, 0.35f), 1 * s);
            g.DrawPath(pen, path);
        }
        Theme.IconBulb(g, new PointF(r.X + 17 * s, r.Y + 17 * s), 18 * s, gold, 1.8f * s);
        var head = Settings.Current.Text.Why;
        using (var hb = new SolidBrush(gold)) g.DrawString(head.ToUpperInvariant(), Theme.Font(12 * s, true), hb, r.X + 32 * s, r.Y + 8 * s);
        using var tb = new SolidBrush(Palette.Text);
        float ty = r.Y + 10 * s + Theme.Font(12 * s, true).Height;
        g.DrawString(why, Theme.Font(13.5f * s), tb, new RectangleF(r.X + 32 * s, ty, ContentWidth - 40 * s, r.Bottom - ty), Theme.Wrap);
    }

    private float NoteHeight((string? title, string text, Color color) n)
    {
        float s = _s, w = ContentWidth - (Busy(_model) ? 44 : 34) * s;
        float h = Theme.Measure(n.text, Theme.Font(13.5f * s), w).Height;
        if (n.title != null) h += Theme.Measure(n.title, Theme.Font(14 * s, true), w).Height + 2 * s;
        return h + 16 * s;
    }

    private void DrawNote(Graphics g, (string? title, string text, Color color) n, float y)
    {
        float s = _s, h = NoteHeight(n), x = Pad, w = ContentWidth;
        var r = new RectangleF(x, y, w, h);
        using (var path = Theme.Rounded(r, 8 * s))
        using (var b = new SolidBrush(Palette.InkRaised)) g.FillPath(b, path);
        float tx = x + 22 * s, ty = y + 8 * s, tw = w - 34 * s;
        if (Busy(_model))
        {
            // spinner: a rotating arc plus a soft sweep along the bottom edge
            var c = new PointF(x + 16 * s, y + h / 2);
            float rr = 7.5f * s, ang = (Environment.TickCount % 1100) / 1100f * 360f;
            using (var track = new Pen(Palette.WithAlpha(n.color, 0.25f), 2.4f * s)) g.DrawEllipse(track, c.X - rr, c.Y - rr, 2 * rr, 2 * rr);
            using (var arc = new Pen(n.color, 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(arc, c.X - rr, c.Y - rr, 2 * rr, 2 * rr, ang, 100);
            float t = (Environment.TickCount % 1600) / 1600f;
            float sw = w * 0.28f, sx = x + (w + sw) * t - sw;
            var sweep = RectangleF.Intersect(new RectangleF(sx, r.Bottom - 3 * s, sw, 3 * s), r);
            if (sweep.Width > 0)
                using (var sb = new LinearGradientBrush(new RectangleF(sx - 1, sweep.Y, sw + 2, sweep.Height), Color.Transparent, n.color, 0f)
                       { WrapMode = WrapMode.TileFlipX })
                    g.FillRectangle(sb, sweep);
            tx = x + 32 * s; tw = w - 44 * s;
        }
        else
        {
            using var bar = Theme.Rounded(new RectangleF(x + 6 * s, y + 7 * s, 4 * s, h - 14 * s), 2 * s);
            using var b = new SolidBrush(n.color);
            g.FillPath(b, bar);
        }
        if (n.title != null)
        {
            var tf = Theme.Font(14 * s, true);
            var th = Theme.Measure(n.title, tf, tw).Height;
            using var tb = new SolidBrush(Palette.Text);
            g.DrawString(n.title, tf, tb, new RectangleF(tx, ty, tw, th + 2), Theme.Wrap);
            ty += th + 2 * s;
        }
        using var db = new SolidBrush(Palette.Text);
        g.DrawString(n.text, Theme.Font(13.5f * s), db, new RectangleF(tx, ty, tw, r.Bottom - ty), Theme.Wrap);
    }

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
        var loc = Settings.Current.Text;
        var head = m.State == "waiting" ? loc.Waiting : $"{loc.Step} {m.Index + 1} / {m.Total}";
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

        if (m.Why != null)
        {
            y += 2 * s;
            DrawWhy(g, m.Why, y);
            y += WhyHeight(m.Why) + 6 * s;
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
                // done = green tick + green text; no strike-through (it reads as "disabled")
                using var tb = new SolidBrush(done ? Palette.Success : Palette.Text);
                g.DrawString(label, Theme.Font(14 * s), tb, new RectangleF(Pad + 26 * s, y + 2 * s, ContentWidth - 26 * s, 22 * s), Theme.Wrap);
                y += 26 * s;
            }
        }
        else if (m.Required > 1)
        {
            using var b = new SolidBrush(Palette.TextDim);
            g.DrawString(string.Format(loc.Marked, m.Done, m.Required), Theme.Font(13 * s), b, Pad, y + 2 * s);
            y += 22 * s;
        }

        if (Note(m) is { } note)
        {
            y += 10 * s;
            DrawNote(g, note, y);
        }
        foreach (var bt in _buttons) DrawButton(g, bt);

        float hh = HintHeight();
        float hy = ClientSize.Height - Pad * 0.6f - hh;
        using (var hb = new SolidBrush(Palette.WithAlpha(Palette.TextDim, 0.75f)))
            g.DrawString(Settings.Current.HintLine(m.State == "waiting"),
                Theme.Font(11.5f * s), hb, new RectangleF(Pad, hy, ContentWidth, hh + 2), Theme.Wrap);
    }

    private void DrawButton(Graphics g, Btn b)
    {
        float s = _s;
        bool hover = _hover == b;
        if (b.Tile) { DrawTile(g, b, hover); return; }
        bool lit = b.Enabled || b.Status;
        Pill(g, b.Rect, b.Style, hover && b.Enabled, lit, Palette.Accent, s);
        var fg = Palette.WithAlpha(TextOn(b.Style), lit ? 1 : 0.45f);
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
            case "help": Theme.IconQuestion(g, ic, 16 * s, fg, 2f * s); break;
        }
        if (textW > 0)
            using (var tb = new SolidBrush(fg))
                g.DrawString(b.Text, f, tb, new RectangleF(tx, b.Rect.Y, textW + 2, b.Rect.Height), new StringFormat(Theme.Center) { Alignment = StringAlignment.Near });
    }

    private void DrawTile(Graphics g, Btn b, bool hover)
    {
        float s = _s;
        var r = b.Rect;
        using (var path = Theme.Rounded(r, 10 * s))
        {
            Color fill = b.Style == "active" ? Palette.WithAlpha(Palette.Accent, 0.22f) : hover && b.Enabled ? Palette.InkHover : Palette.InkRaised;
            using (var br = new SolidBrush(b.Enabled ? fill : Palette.WithAlpha(Palette.InkRaised, 0.5f))) g.FillPath(br, path);
            var edge = b.Style == "active" ? Palette.Accent : hover && b.Enabled ? Palette.WithAlpha(Palette.Accent, 0.6f) : Palette.Line;
            using var pen = new Pen(b.Enabled ? edge : Palette.WithAlpha(Palette.Line, 0.5f), 1 * s);
            g.DrawPath(pen, path);
        }
        var fg = Palette.WithAlpha(Palette.Text, b.Enabled ? 1 : 0.35f);
        var ic = new PointF(r.X + r.Width / 2, r.Y + 19 * s);
        switch (b.Icon)
        {
            case "back": Theme.IconChevron(g, ic, 14 * s, true, fg, 2.3f * s); break;
            case "undo": Theme.IconUndo(g, ic, 19 * s, fg, 2.1f * s); break;
            case "skip": Theme.IconSkip(g, ic, 12 * s, fg, 2.1f * s); break;
            case "help": Theme.IconQuestion(g, ic, 17 * s, fg, 2.1f * s); break;
        }
        using var tb = new SolidBrush(Palette.WithAlpha(b.Enabled ? Palette.Text : Palette.TextDim, b.Enabled ? 0.92f : 0.5f));
        g.DrawString(b.Text, Theme.Font(12 * s, true), tb, new RectangleF(r.X + 2 * s, r.Y + 33 * s, r.Width - 4 * s, 18 * s), Theme.Center);
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
        else if (b.Action == "help_open") { _helpOpen = true; _helpStepVersion = _model.StepVersion; Relayout(); }
        else if (b.Action == "help_cancel") { _helpOpen = false; Relayout(); }
        else if (b.Action == "help_other")
        {
            _helpOpen = false;
            Relayout();
            // the circled area is described via UI Automation — keep that off the UI thread
            HelpInput.Ask(this, _s, (text, sketch) =>
            {
                if (sketch == null) Engine.RequestHelp("other", "card", text);
                else System.Threading.Tasks.Task.Run(() => Engine.RequestHelp("other", "card", text, sketch));
            });
        }
        else
        {
            if (b.Action is "cant_find" or "dont_understand") _helpOpen = false;
            Engine.UserAction(b.Action!, "card");
        }
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

/// <summary>
/// "Something else": a small focused window where the user types the problem in their own words.
/// The card can't take keyboard input (it never activates, so the app keeps focus) — this window does,
/// briefly, and hands focus back to whatever was in front before.
/// </summary>
internal sealed class HelpInput : Form
{
    private static HelpInput? _open;
    private readonly TextBox _box;
    private readonly IntPtr _returnTo;
    private readonly Action<string, Sketch?> _send;
    private readonly float _s;
    private readonly Sketch _sketch = new();
    private bool _sketching;                   // hidden while the user draws — not a "click elsewhere"
    private bool _disposeSketch = true;        // false once the sketch was handed to the engine
    private RectangleF _sendRect, _cancelRect, _pencilRect;
    private string _hover = "";

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    public static void Ask(Form near, float scale, Action<string, Sketch?> send)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new HelpInput(near, scale, send);
        _open.FormClosed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
        _open._box.Focus();
    }

    private HelpInput(Form near, float s, Action<string, Sketch?> send)
    {
        _s = s;
        _send = send;
        _returnTo = GetForegroundWindow();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Palette.Ink;
        KeyPreview = true;
        Text = "TutorialOverlay help";
        int w = (int)(near.Width), h = (int)(222 * s);
        var work = Native.WorkAreaAt(new PointF(near.Left + near.Width / 2f, near.Top));
        int y = near.Top - h - (int)(10 * s);
        if (y < work.Top) y = Math.Min(near.Bottom + (int)(10 * s), work.Bottom - h);
        Bounds = new Rectangle(near.Left, y, w, h);
        _box = new TextBox
        {
            Multiline = true, BorderStyle = BorderStyle.None, BackColor = Palette.InkRaised, ForeColor = Palette.Text,
            Font = Theme.Font(14.5f * s), AcceptsReturn = false, ScrollBars = ScrollBars.None,
            Bounds = Rectangle.Round(new RectangleF(30 * s, 52 * s, w - 60 * s, 70 * s)),
        };
        Controls.Add(_box);
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == System.Windows.Forms.Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Submit(); }
            else if (e.KeyCode == System.Windows.Forms.Keys.Enter && e.Shift) { e.SuppressKeyPress = true; _box.SelectedText = Environment.NewLine; }
            else if (e.KeyCode == System.Windows.Forms.Keys.Escape) { e.SuppressKeyPress = true; CloseAndReturn(); }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // clicking elsewhere = cancel; never leave a stray input window behind
        if (Visible && !_sketching) BeginInvoke(new Action(() => { if (!IsDisposed) Close(); }));
    }

    private void Submit()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0) { _box.Focus(); return; }
        if (_sketch.Empty) { _sketch.Dispose(); _send(text, null); }
        else _send(text, _sketch);           // the receiver owns the sketch now
        CloseAndReturn(false);
    }

    /// <summary>Pencil: hide, let the user circle the area on a frozen screen, come back with the text intact.</summary>
    private void StartSketch()
    {
        _sketching = true;
        Hide();
        // give DWM a moment to drop this window before the screen is frozen
        var t = new System.Windows.Forms.Timer { Interval = 120 };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            if (IsDisposed) return;
            SketchPad.Open(_sketch, _ =>
            {
                if (IsDisposed) return;
                Show();
                Activate();
                _box.Focus();
                _sketching = false;
                Invalidate();
            });
        };
        t.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (_disposeSketch) _sketch.Dispose();
    }

    private void CloseAndReturn(bool dropSketch = true)
    {
        _disposeSketch = dropSketch;
        var back = _returnTo;
        Close();
        if (back != IntPtr.Zero) SetForegroundWindow(back);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Quality(g);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = _s, w = ClientSize.Width, h = ClientSize.Height;
        var loc = Settings.Current.Text;
        g.Clear(Palette.Ink);
        using (var b = new LinearGradientBrush(new RectangleF(0, 0, w, 3 * s), Palette.Accent, Palette.Named["purple"], 0f))
            g.FillRectangle(b, 0, 0, w, 3 * s);
        using (var pen = new Pen(Palette.Line, 1)) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
        using (var tb = new SolidBrush(Palette.Text)) g.DrawString(loc.OtherTitle, Theme.Font(16 * s, true), tb, 18 * s, 16 * s);
        var field = new RectangleF(18 * s, 44 * s, w - 36 * s, 86 * s);
        using (var path = Theme.Rounded(field, 8 * s))
        {
            using (var b = new SolidBrush(Palette.InkRaised)) g.FillPath(b, path);
            using var pen = new Pen(_box.Focused ? Palette.Accent : Palette.Line, 1.2f * s);
            g.DrawPath(pen, path);
        }
        using (var hb = new SolidBrush(Palette.WithAlpha(Palette.TextDim, 0.8f)))
            g.DrawString(loc.OtherHint, Theme.Font(11 * s), hb, new RectangleF(18 * s, field.Bottom + 6 * s, w - 36 * s, 18 * s), new StringFormat(Theme.Center) { Alignment = StringAlignment.Near, FormatFlags = 0 });
        float bh = 34 * s, by = h - bh - 14 * s;
        _sendRect = new RectangleF(w - 18 * s - 120 * s, by, 120 * s, bh);
        _cancelRect = new RectangleF(_sendRect.X - 10 * s - 96 * s, by, 96 * s, bh);
        _pencilRect = new RectangleF(18 * s, by, _cancelRect.X - 10 * s - 18 * s, bh);
        DrawPill(g, _cancelRect, loc.Cancel, false, _hover == "cancel");
        DrawPill(g, _sendRect, loc.Send, true, _hover == "send");
        DrawPencil(g, _pencilRect, _sketch.Empty ? loc.Sketch : string.Format(loc.Sketched, _sketch.Strokes.Count), !_sketch.Empty, _hover == "pencil");
    }

    private void DrawPencil(Graphics g, RectangleF r, string text, bool on, bool hover)
    {
        using var path = Theme.Rounded(r, r.Height / 2);
        using (var b = new SolidBrush(hover ? Palette.InkHover : Palette.InkRaised)) g.FillPath(b, path);
        using (var pen = new Pen(on ? Palette.Accent : Palette.Line, (on ? 1.5f : 1f) * _s)) g.DrawPath(pen, path);
        var fg = on ? Palette.Accent : Palette.Text;
        float ic = 16 * _s, pad = 12 * _s;
        var font = Theme.Font(13.5f * _s, true);
        float tw = Math.Min(Theme.Measure(text, font, r.Width).Width, r.Width - pad * 2 - ic - 6 * _s);
        float x = r.X + (r.Width - ic - 6 * _s - tw) / 2;
        Theme.IconPencil(g, new PointF(x + ic / 2, r.Y + r.Height / 2), ic, fg, 1.8f * _s);
        using var tb = new SolidBrush(fg);
        g.DrawString(text, font, tb, new RectangleF(x + ic + 6 * _s, r.Y, tw + 2 * _s, r.Height), new StringFormat(Theme.Center) { Alignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter });
    }

    private void DrawPill(Graphics g, RectangleF r, string text, bool primary, bool hover)
    {
        using var path = Theme.Rounded(r, r.Height / 2);
        var fill = primary ? (hover ? Color.FromArgb(255, 255, 110, 135) : Palette.Accent) : (hover ? Palette.InkHover : Palette.InkRaised);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!primary) using (var pen = new Pen(Palette.Line, 1 * _s)) g.DrawPath(pen, path);
        using var tb = new SolidBrush(primary ? Palette.Ink : Palette.Text);
        g.DrawString(text, Theme.Font(13.5f * _s, true), tb, r, Theme.Center);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hv = _sendRect.Contains(e.Location) ? "send" : _cancelRect.Contains(e.Location) ? "cancel" : _pencilRect.Contains(e.Location) ? "pencil" : "";
        if (hv != _hover) { _hover = hv; Cursor = hv.Length > 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_sendRect.Contains(e.Location)) Submit();
        else if (_cancelRect.Contains(e.Location)) CloseAndReturn();
        else if (_pencilRect.Contains(e.Location)) StartSketch();
    }
}
