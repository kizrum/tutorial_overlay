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

    /// <summary>Moves and sizes the window in one go; a new size drops the old pixels and paints at once.</summary>
    public virtual void MoveTo(Rectangle r)
    {
        if (Bounds == r) return;
        bool resized = r.Size != Size;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height,
            Native.SWP_NOACTIVATE | (resized ? Native.SWP_NOCOPYBITS : 0));
        if (resized) { Invalidate(); Update(); }
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
/// The step card, "outline" look: header (step n of N, a mini progress strip, close), title, text, a folded "why",
/// checklist, one message, a row of actions (Back / Undo / Skip as quiet icons, Help, Done) and the agent's buttons.
/// It positions itself away from every target and label, and the user can drag it (then it stays put).
/// When only its height changes (the "why" opens, a hint arrives) it keeps the edge nearest the screen edge,
/// so a card in a bottom corner grows upward and its buttons stay under the cursor.
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
        public bool IconOnly;      // quiet round icon button; its name shows in the bottom line on hover
        public bool Left;          // content aligned left (the help choices)
        public bool Status;        // shows a state ("Got it!"): drawn bright, but not clickable
        public bool Offline;       // needs the agent, which is away: dimmed, a click only says to write to it in the chat
    }

    private enum Part { Head, Banner, Title, Text, WhyRow, WhyBody, Check, Marked, Note, Hint, Thread, Compose, ChatRow }

    private CardModel _model = new();
    private string _sig = "";
    private float _s = 1;
    private Size _size = new(10, 10);
    private readonly List<Btn> _buttons = new();
    private readonly List<(Part part, RectangleF r, int index)> _parts = new();
    private Btn? _hover, _pressed;
    private RectangleF _closeRect, _whyRect, _agentRect;
    private bool _hoverClose, _hoverWhy, _hoverAgent;
    // a dimmed button was pressed: the bottom line says what to do instead, for a few seconds
    private string? _flash;
    private long _flashUntil;
    private const int FlashMs = 4000;
    // the step's conversation ("Something else"): the card shows it instead of the step until the user goes back
    private bool _chatOpen;
    private readonly Dictionary<int, int> _seen = new();   // per step: messages the user has seen (more: a dot on its row)
    private long _replyTick;                    // the last "answer waits on another step" the card has told about
    private int ChatSeen => _seen.GetValueOrDefault(_model.Index);
    private RectangleF _backRect, _composeRect, _chatRowRect;
    private bool _hoverChat;
    private ComposeBox? _compose;               // the line the user types into, laid exactly over _composeRect
    private string _draft = "";                 // what was typed and not sent (kept when the user clicks away)
    private Sketch? _draftSketch;               // and what was marked with it
    private readonly List<Bubble> _bubbles = new();
    private readonly Dictionary<string, Bitmap?> _thumbs = new();

    private sealed class Bubble
    {
        public ChatLine? Line;
        public bool Typing;
        public RectangleF R;                    // relative to the thread's top left
        public float LabelH, TitleH, TextH, ImgH;
    }
    private Point? _dragFrom;
    private Point _dragOrigin;
    private bool _pinned;
    private int _pinnedStepVersion = -1;
    private bool _helpOpen;                 // "?" pressed: showing the help choices
    private int _helpStepVersion = -1;
    private string _hotkeySig = "";
    private readonly Dictionary<int, string> _hotkeyIds = new();
    private Rectangle _target;
    // "why": folded to one line; opened, it stays open for the rest of the lesson. The height is eased, never jumps.
    private object? _lesson;
    private bool _whyOpen;
    private float _whyFrac;
    private long _whyTick;
    private float _whyFull;

    private const float WhyMs = 200f;

    public CardWindow() { Text = "TutorialOverlay step"; Size = new Size(10, 10); }

    // ------------------------------------------------------------------ layered window
    // The card is a layered window: each frame is drawn into a picture and handed over together with the window's
    // place and size (UpdateLayeredWindow). A plain window shows its new size a frame before its new picture, the
    // old one pinned to the top: a card growing upward ("why") had its buttons jump up and back every frame.

    private bool _dirty = true, _flushQueued;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_LAYERED; return cp; }
    }

    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        _dirty = true;
        if (_flushQueued || !IsHandleCreated) return;
        _flushQueued = true;
        BeginInvoke(() => { _flushQueued = false; if (_dirty) Present(Bounds); });
    }

    public override void MoveTo(Rectangle r)
    {
        if (r == Bounds && !_dirty) return;
        if (r.Size == Size && !_dirty)
        {
            // only moved (dragged): the picture stays
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
            return;
        }
        Present(r);
    }

    private void Present(Rectangle r)
    {
        if (!IsHandleCreated || r.Width <= 0 || r.Height <= 0) return;
        _dirty = false;
        using var content = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(content)) PaintCard(g, r.Size);
        using var frame = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            // rounded corners of our own, smoothed (the system rounds only plain windows)
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float rad = 8 * _s;
            using (var path = Theme.Rounded(new RectangleF(0, 0, r.Width, r.Height), rad))
            using (var tb = new TextureBrush(content)) g.FillPath(tb, path);
            using (var edge = Theme.Rounded(new RectangleF(0.5f, 0.5f, r.Width - 1, r.Height - 1), rad - 0.5f))
            using (var pen = new Pen(Palette.Line, 1)) g.DrawPath(pen, edge);
        }
        Native.PresentLayered(Handle, frame, r);
        UpdateBounds();     // the window moved without telling WinForms: Bounds would still say the old place and size
    }

    public void UpdateModel(CardModel m)
    {
        if (!m.Visible)
        {
            Engine.CardRect = null;
            CloseCompose();
            if (Visible) Hide();
            SetHotkeys(false);
            return;
        }
        SetHotkeys(true);
        // a conversation belongs to its step: a new step (or lesson, or the end card) starts without one open
        if (_chatOpen && (m.Index != _model.Index || !ReferenceEquals(m.Lesson, _lesson) || m.State is "waiting" or "outro")) CloseChat(true);
        if (_chatOpen) _seen[m.Index] = m.Chat.Count;
        if (!ReferenceEquals(m.Lesson, _lesson)) { _lesson = m.Lesson; _whyOpen = false; _whyFrac = 0; _seen.Clear(); }
        if (m.ReplyTick != _replyTick)
        {
            _replyTick = m.ReplyTick;
            if (m.ReplyOn >= 0 && m.ReplyOn != m.Index) Flash(string.Format(Settings.Current.Text.ReplyElsewhere, m.ReplyOn + 1), 8000);
        }
        if (_helpOpen && _helpStepVersion != m.StepVersion && m.Index != _model.Index) _helpOpen = false;
        StepWhy();
        float s = Native.ScaleAt(m.Focus == default ? new PointF(Location.X, Location.Y) : m.Focus);
        var sig = Signature(m, s) + (_helpOpen ? "|help" : "") + "|why" + _whyOpen + ":" + _whyFrac.ToString("0.000")
            + "|chat" + _chatOpen + ":" + m.Chat.Count + ":" + (m.Chat.Count > 0 ? m.Chat[^1].Text : "") + ":" + _seen.GetValueOrDefault(m.Index)
            + ":" + _draft + ":" + (_draftSketch?.Strokes.Count ?? 0);
        _model = m;
        if (sig != _sig || Math.Abs(s - _s) > 0.01f)
        {
            _sig = sig;
            _s = s;
            _size = Measure();              // placement below decides the location
            Invalidate();
        }
        if (Busy(m)) Invalidate();          // the spinner keeps turning while we wait for the assistant
        if (_flash != null && Environment.TickCount64 > _flashUntil) { _flash = null; Invalidate(); }
        if (_pinned && _pinnedStepVersion != m.StepVersion && Intersects(Bounds, m.Avoid)) _pinned = false;
        var work = Native.WorkAreaAt(m.Focus == default ? new PointF(0, 0) : m.Focus);
        // while the user holds the card, only the hand moves it (pulling it back here fought the drag at the screen's
        // edge and the taskbar: the card shook); dropped, it keeps to the monitor it was dropped on
        if (_dragFrom != null) { if (Size != _size) MoveTo(new Rectangle(Location, _size)); }
        else if (!_pinned) Place(m, work);
        else MoveTo(Anchored(Bounds, _size, Native.WorkAreaAt(new PointF(Left + Width / 2f, Top + Height / 2f))));
        ShowQuiet();
        Engine.CardRect = Bounds;
        _compose?.Follow(ComposeScreenRect());
    }

    private static string Signature(CardModel m, float s) =>
        string.Join("\u0001", Settings.Current.Lang, Settings.Current.Accent.ToArgb(), Settings.Current.HotkeySignature, m.State, m.Index, m.Prep, m.Total, m.Title, m.Text, m.Done, m.Required, m.Completed, m.CanBack, m.CanUndo,
            string.Join(",", m.Checklist.Select(c => c.label + c.done)), string.Join(",", m.Extra.Select(b => b.Id + b.Text)),
            string.Join(",", m.Controls), m.Why, m.Hint, m.HintTitle, m.Issue, m.HelpPending, m.Highlighting, m.Review, m.Checking, m.Agent, s);

    /// <summary>Moves the "why" height a frame's worth toward open or closed (the UI tick calls us every ~30 ms).</summary>
    private void StepWhy()
    {
        float target = _whyOpen ? 1 : 0;
        long now = Environment.TickCount64;
        float dt = _whyTick == 0 ? 1 : (now - _whyTick) / WhyMs;
        _whyTick = now;
        if (!Engine.Animate || _model.Why == null) _whyFrac = target;
        else if (_whyFrac < target) _whyFrac = Math.Min(target, _whyFrac + dt);
        else if (_whyFrac > target) _whyFrac = Math.Max(target, _whyFrac - dt);
    }

    private float WhyEase => 1 - (1 - _whyFrac) * (1 - _whyFrac) * (1 - _whyFrac);

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
    private float Radius => 6 * _s;
    private Font HeadFont(bool bold) => Theme.Font(13 * _s, bold);
    private Font TitleFont => Theme.Font(17.5f * _s, true);
    private Font BodyFont => Theme.Font(14.5f * _s);
    private Font SmallFont => Theme.Font(13.5f * _s);
    private Font HintFont => Theme.Font(11.5f * _s);

    /// <summary>Lays the card out once (parts and buttons with their rectangles); painting only follows it.</summary>
    private Size Measure()
    {
        var m = _model;
        float s = _s, y = Pad, x = Pad, w = ContentWidth;
        _parts.Clear();
        _whyRect = RectangleF.Empty;
        _backRect = _composeRect = _chatRowRect = RectangleF.Empty;
        void Block(Part p, float h, float before, int index = 0)
        {
            y += before;
            _parts.Add((p, new RectangleF(x, y, w, h), index));
            y += h;
        }

        Block(Part.Head, 28 * s, 0);
        if (_chatOpen)
        {
            // the conversation, the line to type into, the keys
            Block(Part.Thread, LayoutThread(w), 10 * s);
            Block(Part.Compose, 42 * s, 12 * s);
            _composeRect = _parts[^1].r;
            _buttons.Clear();
            Block(Part.Hint, HintFont.Height, 10 * s);
            return new Size((int)Math.Ceiling(ContentWidth + Pad * 2), (int)Math.Ceiling(y + Pad * 0.7f));
        }
        var note = Note(m);
        if (note is { top: true } banner) Block(Part.Banner, NoteHeight(banner), 10 * s);
        bool help = _helpOpen && m.State is not ("waiting" or "outro");
        if (m.Title.Length > 0) Block(Part.Title, Theme.Measure(m.Title, TitleFont, w).Height, 8 * s);
        if (!help)
        {
            if (m.Text.Length > 0) Block(Part.Text, Theme.Measure(m.Text, BodyFont, w).Height, 6 * s);
            if (m.Why != null)
            {
                Block(Part.WhyRow, 22 * s, 10 * s);
                _whyRect = _parts[^1].r;
                _whyFull = Theme.Measure(m.Why, SmallFont, w - 16 * s).Height + 8 * s;
                float open = _whyFull * WhyEase;
                if (open >= 1) Block(Part.WhyBody, open, 0);
            }
            if (m.Chat.Count > 0)
            {
                // the conversation folded into one line, like "why"
                Block(Part.ChatRow, 32 * s, 10 * s);
                _chatRowRect = _parts[^1].r;
            }
            if (m.Checklist.Count > 0)
            {
                y += 6 * s;
                for (int i = 0; i < m.Checklist.Count; i++) Block(Part.Check, 26 * s, 0, i);
            }
            else if (m.Required > 1) Block(Part.Marked, 22 * s, 6 * s);
            if (note is { top: false } low) Block(Part.Note, NoteHeight(low), 12 * s);
        }
        BuildButtons(ref y);
        if (m.State is not ("waiting" or "outro") || HintHeight() > 0 || m.Agent == "off")
            Block(Part.Hint, Math.Max(HintHeight(), HintFont.Height), 10 * s);
        return new Size((int)Math.Ceiling(ContentWidth + Pad * 2), (int)Math.Ceiling(y + Pad * 0.7f));
    }

    private void BuildButtons(ref float y)
    {
        var m = _model;
        float s = _s, h = 36 * s, gap = 8 * s, x0 = Pad, right = Pad + ContentWidth;
        _buttons.Clear();

        if (m.Extra.Count > 0)
        {
            y += 12 * s;
            float x = x0, bh = 34 * s;
            foreach (var b in m.Extra)
            {
                bool off = b.Id == "outro_continue" && m.Agent == "off";   // the next lesson is the agent's to write
                float w = TextW(b.Text) + 28 * s + (off ? 22 * s : 0);
                if (x + w > right && x > x0) { x = x0; y += bh + gap; }
                _buttons.Add(new Btn { Id = b.Id, Text = b.Text, Style = b.Style, Action = b.Action, Custom = true, Offline = off, Rect = new RectangleF(x, y, Math.Min(w, ContentWidth), bh) });
                x += w + gap;
            }
            y += bh;
        }

        if (m.State is "waiting" or "outro") return;
        var loc = Settings.Current.Text;
        if (_helpOpen)
        {
            // the kinds of trouble, one per line, then a way out
            y += 12 * s;
            float lh = 40 * s;
            foreach (var (id, text, icon) in new[] { ("cant_find", loc.CantFind, "search"), ("dont_understand", loc.DontGet, "bulb"), ("help_other", loc.Other, "message") })
            {
                // "something else" is a question for the agent; the other two the card answers itself
                _buttons.Add(new Btn { Id = id, Action = id, Text = text, Icon = icon, Left = true, Offline = id == "help_other" && m.Agent == "off", Rect = new RectangleF(x0, y, ContentWidth, lh) });
                y += lh + gap;
            }
            float cw = Math.Max(96 * s, TextW(loc.Cancel) + 32 * s);
            _buttons.Add(new Btn { Id = "help_cancel", Action = "help_cancel", Text = loc.Cancel, Style = "ghost", Rect = new RectangleF(right - cw, y, cw, h) });
            y += h;
            return;
        }

        // quiet icons on the left; only the ones that can do something now are shown
        var left = new List<Btn>();
        if (m.Controls.Contains("back") && m.CanBack)
            left.Add(new Btn { Id = "back", Action = "back", Icon = "back", Text = loc.Back, IconOnly = true });
        if (m.Controls.Contains("undo") && m.CanUndo)
            left.Add(new Btn { Id = "undo", Action = "undo", Icon = "undo", Text = loc.Undo, IconOnly = true });
        if (m.Controls.Contains("skip") && !m.Completed)
            left.Add(new Btn { Id = "skip", Action = "skip", Icon = "skip", Text = loc.Skip, IconOnly = true });
        // Help and Done on the right, Done at the very corner: "forward" is always in the same place
        var rightBtns = new List<Btn>();
        if (m.Controls.Contains("help") && !m.Completed)
            rightBtns.Add(new Btn { Id = "help", Action = "help_open", Icon = "help", Text = loc.Help, Style = m.HelpPending ? "active" : "secondary" });
        if (m.Controls.Contains("done"))
            rightBtns.Add(new Btn
            {
                Id = "done", Action = "done", Icon = "check", Text = m.Completed ? loc.Got : loc.Done,
                Style = m.Completed ? "success" : "primary", Enabled = !m.Completed, Status = m.Completed,
            });
        if (left.Count + rightBtns.Count == 0) return;
        y += 14 * s;
        float lx = x0;
        foreach (var b in left)
        {
            b.Rect = new RectangleF(lx, y, h, h);
            _buttons.Add(b);
            lx += h + 2 * s;
        }
        float rx = right;
        for (int i = rightBtns.Count - 1; i >= 0; i--)
        {
            var b = rightBtns[i];
            float bw = TextW(b.Text) + 18 * s + 6 * s + 30 * s;
            b.Rect = new RectangleF(rx - bw, y, bw, h);
            _buttons.Add(b);
            rx -= bw + gap;
        }
        y += h;
    }

    private float TextW(string t) => Theme.Measure(t, Theme.Font(13.5f * _s, true), 600 * _s).Width;

    private float HintHeight()
    {
        var line = Settings.Current.HintLine(_model.State is "waiting" or "outro");
        return line.Length == 0 ? 0 : Theme.Measure(line, HintFont, ContentWidth).Height;
    }

    /// <summary>
    /// A card that only changed height keeps the edge nearest the screen edge: in the lower half it grows upward,
    /// in the upper half downward — never out of the work area, so placement has no reason to jump.
    /// </summary>
    private static Rectangle Anchored(Rectangle old, Size size, Rectangle work)
    {
        if (old.Width <= 10) return new Rectangle(old.Location, size);
        bool bottom = old.Top + old.Height / 2 > work.Top + work.Height / 2;
        int y = bottom ? old.Bottom - size.Height : old.Top;
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - size.Height));
        int x = Math.Max(work.Left, Math.Min(old.X, work.Right - size.Width));
        return new Rectangle(x, y, size.Width, size.Height);
    }

    /// <summary>Where a dragged card goes: where the hand puts it, stopped whole inside the monitor's work area.</summary>
    internal static Point DragTo(Point wanted, Size size, Rectangle work) => new(
        Math.Clamp(wanted.X, work.Left, Math.Max(work.Left, work.Right - size.Width)),
        Math.Clamp(wanted.Y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));

    private void Place(CardModel m, Rectangle work)
    {
        float s = _s, margin = 16 * s;
        var size = _size;
        var avoid = m.Avoid.Select(r => { r.Inflate(14 * s, 14 * s); return r; }).ToList();
        Rectangle At(float x, float y) => new((int)x, (int)y, size.Width, size.Height);
        // stay where we are while that spot is still free (no jumping around)
        var current = Anchored(Bounds, size, work);
        if (_target != Rectangle.Empty && work.Contains(current) && !Intersects(current, avoid)) { MoveTo(current); return; }
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

    /// <summary>Waiting for the next steps or for an answer to "help": show motion, not a frozen card.</summary>
    private static bool Busy(CardModel m) => m.State == "waiting" || (m.HelpPending && m.Hint == null) || m.Checking;

    /// <summary>
    /// One message: the agent's hint, else "checking / sent", else why targets are hidden. A problem that blocks the
    /// step (an issue) goes on top, under the header — it is the first thing to read; the rest sits above the buttons.
    /// </summary>
    private (string? title, string text, Color color, bool top)? Note(CardModel m)
    {
        var loc = Settings.Current.Text;
        if (m.State == "waiting") return (m.HintTitle, m.Hint ?? loc.Preparing, Palette.Named["blue"], false);
        if (m.Hint != null) return (m.HintTitle, m.Hint, Palette.Named["blue"], false);
        if (m.Checking) return (null, loc.Checking, Palette.Named["blue"], false);
        if (m.HelpPending && m.Chat.Count == 0) return (null, m.Highlighting ? loc.Highlighting + " " + loc.HelpSent : loc.HelpSent, Palette.Named["blue"], false);
        if (m.Issue != null) return (null, m.Issue, Palette.Warn, true);
        if (m.Review) return (null, loc.Review, Palette.Named["blue"], false);
        return null;
    }

    private float NoteHeight((string? title, string text, Color color, bool top) n)
    {
        float s = _s, w = ContentWidth - 44 * s;
        float h = Theme.Measure(n.text, SmallFont, w).Height;
        if (n.title != null) h += Theme.Measure(n.title, Theme.Font(14 * s, true), w).Height + 2 * s;
        return h + 18 * s;
    }

    // ------------------------------------------------------------------ paint

    protected override void OnPaint(PaintEventArgs e) { }     // layered: see Present

    private void PaintCard(Graphics g, Size size)
    {
        Theme.Quality(g);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;  // opaque surface: ClearType is fine
        var m = _model;
        g.Clear(Palette.Ink);
        _closeRect = RectangleF.Empty;
        _agentRect = RectangleF.Empty;
        var note = Note(m);
        foreach (var (part, r, index) in _parts)
        {
            switch (part)
            {
                case Part.Head: DrawHead(g, r); break;
                case Part.Banner: if (note != null) DrawNote(g, note.Value, r); break;
                case Part.Note: if (note != null) DrawNote(g, note.Value, r); break;
                case Part.Title:
                    using (var b = new SolidBrush(Palette.Text)) g.DrawString(m.Title, TitleFont, b, new RectangleF(r.X, r.Y, r.Width, r.Height + 2), Theme.Wrap);
                    break;
                case Part.Text:
                    using (var b = new SolidBrush(Palette.Text)) g.DrawString(m.Text, BodyFont, b, new RectangleF(r.X, r.Y, r.Width, r.Height + 2), Theme.Wrap);
                    break;
                case Part.WhyRow: DrawWhyRow(g, r); break;
                case Part.WhyBody: DrawWhyBody(g, r); break;
                case Part.Check: if (index < m.Checklist.Count) DrawCheck(g, r, m.Checklist[index]); break;
                case Part.Marked:
                    using (var b = new SolidBrush(Palette.TextDim)) g.DrawString(string.Format(Settings.Current.Text.Marked, m.Done, m.Required), Theme.Font(13 * _s), b, r.X, r.Y + 2 * _s);
                    break;
                case Part.Hint: DrawHint(g, r); break;
                case Part.Thread: DrawThread(g, r); break;
                case Part.Compose: DrawCompose(g, r); break;
                case Part.ChatRow: DrawChatRow(g, r); break;
            }
        }
        foreach (var bt in _buttons) DrawButton(g, bt);
    }

    /// <summary>"Step 3 of 8", a strip of short segments next to the close cross (no row of its own), and the cross.</summary>
    private void DrawHead(Graphics g, RectangleF r)
    {
        var m = _model;
        var loc = Settings.Current.Text;
        float s = _s, cy = r.Y + r.Height / 2;
        bool waiting = m.State == "waiting", outro = m.State == "outro";
        string a = _chatOpen ? "" : waiting ? loc.HeadWaiting : outro ? loc.HeadOutro : string.Format(loc.HeadStep, m.Prep ? 0 : m.Index + 1);
        string b = _chatOpen || waiting || outro || m.Total == 0 ? "" : string.Format(loc.HeadOf, m.Total);
        var fa = HeadFont(true);
        var fb = HeadFont(false);
        float aw = Theme.Measure(a, fa, 300 * s).Width;
        using (var br = new SolidBrush(outro ? Palette.Success : Palette.Text)) g.DrawString(a, fa, br, r.X, cy - fa.Height / 2f);
        float tx = r.X + aw;
        if (b.Length > 0)
        {
            using var br = new SolidBrush(Palette.TextDim);
            g.DrawString(b, fb, br, tx + 3 * s, cy - fb.Height / 2f);
            tx += 3 * s + Theme.Measure(b, fb, 300 * s).Width;
        }
        if (m.Completed && !_chatOpen)
        {
            var c = new PointF(tx + 15 * s, cy);
            using (var br = new SolidBrush(Palette.Success)) g.FillEllipse(br, c.X - 8 * s, c.Y - 8 * s, 16 * s, 16 * s);
            Theme.IconCheck(g, c, 11 * s, Palette.Ink, 2.2f * s);
            tx += 27 * s;
        }

        if (m.Controls.Contains("stop") || waiting)
        {
            _closeRect = new RectangleF(r.Right - 26 * s, cy - 13 * s, 26 * s, 26 * s);
            if (_hoverClose) using (var br = new SolidBrush(Palette.InkHover)) g.FillEllipse(br, _closeRect);
            Theme.IconCross(g, new PointF(_closeRect.X + 13 * s, cy), 14 * s, _hoverClose ? Palette.Text : Palette.TextDim, 2 * s);
        }

        // the assistant: green while it follows the lesson, grey and struck through while it is away; none for a replay
        float edge = _closeRect.IsEmpty ? r.Right : _closeRect.X - 2 * s;
        if (m.Agent.Length > 0)
        {
            bool on = m.Agent == "on";
            _agentRect = new RectangleF(edge - 24 * s, cy - 12 * s, 24 * s, 24 * s);
            var color = on ? Palette.Success : Palette.WithAlpha(Palette.TextDim, _hoverAgent ? 1f : 0.75f);
            Theme.IconAgent(g, new PointF(_agentRect.X + 12 * s, cy + 1 * s), 16 * s, color, 1.6f * s, !on);
            edge = _agentRect.X;
        }

        if (_chatOpen) { DrawBack(g, r, edge); return; }

        // the mini progress strip
        if (m.Total <= 0) return;
        float stripRight = edge - 8 * s;
        float avail = stripRight - (tx + 20 * s);
        int passed = outro ? m.Total : Math.Clamp((m.Prep ? 0 : m.Index) + (m.Completed ? 1 : 0), 0, m.Total);
        int now = outro || waiting || m.Prep || m.Completed ? -1 : m.Index;
        float segW = (m.Total > 12 ? 4 : 7) * s, gap = 2 * s, h = 3 * s;
        float width = m.Total * segW + (m.Total - 1) * gap;
        if (width <= avail && width <= 200 * s)
        {
            float x = stripRight - width;
            for (int i = 0; i < m.Total; i++)
            {
                var seg = new RectangleF(x + i * (segW + gap), cy - h / 2, segW, h);
                Color c = i < passed ? Palette.Accent : i == now ? Palette.WithAlpha(Palette.Accent, 0.5f) : Palette.Line;
                using var path = Theme.Rounded(seg, 1 * s);
                using var br = new SolidBrush(c);
                g.FillPath(br, path);
            }
        }
        else if (avail > 24 * s)
        {
            // too many steps for segments: one short bar
            float bw = Math.Min(avail, 90 * s);
            var track = new RectangleF(stripRight - bw, cy - h / 2, bw, h);
            using (var path = Theme.Rounded(track, 1 * s))
            using (var br = new SolidBrush(Palette.Line)) g.FillPath(br, path);
            float fw = bw * passed / m.Total;
            if (fw > 0.5f)
                using (var path = Theme.Rounded(new RectangleF(track.X, track.Y, fw, h), 1 * s))
                using (var br = new SolidBrush(Palette.Accent)) g.FillPath(br, path);
        }
    }

    /// <summary>The folded "why": a bulb, the label, the start of the text (while folded), a chevron.</summary>
    private void DrawWhyRow(Graphics g, RectangleF r)
    {
        float s = _s, cy = r.Y + r.Height / 2;
        var gold = Palette.Named["yellow"];
        if (_hoverWhy) gold = Lighten(gold, 0.25f);
        Theme.IconBulb(g, new PointF(r.X + 8 * s, cy + 1 * s), 16 * s, gold, 1.7f * s);
        var lf = Theme.Font(13 * s, true);
        var label = Settings.Current.Text.Why;
        float lw = Theme.Measure(label, lf, 300 * s).Width;
        using (var b = new SolidBrush(gold)) g.DrawString(label, lf, b, r.X + 22 * s, cy - lf.Height / 2f);
        Theme.IconChevronV(g, new PointF(r.Right - 7 * s, cy), 10 * s, _whyOpen, _hoverWhy ? Palette.Text : Palette.TextDim, 1.8f * s);
        if (!_whyOpen && _model.Why != null)
        {
            float px = r.X + 22 * s + lw + 8 * s, pw = r.Right - 22 * s - px;
            if (pw > 30 * s)
            {
                using var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
                using var b = new SolidBrush(Palette.WithAlpha(Palette.TextDim, 0.8f));
                g.DrawString(_model.Why.Replace('\n', ' '), SmallFont, b, new RectangleF(px, r.Y, pw, r.Height), fmt);
            }
        }
    }

    /// <summary>The open "why": clipped to the eased height, so it unrolls instead of popping in.</summary>
    private void DrawWhyBody(Graphics g, RectangleF r)
    {
        if (_model.Why == null) return;
        float s = _s;
        var state = g.Save();
        g.SetClip(r);
        float a = Math.Clamp(WhyEase, 0, 1);
        using (var pen = new Pen(Palette.WithAlpha(Palette.Named["yellow"], 0.45f * a), 2 * s))
            g.DrawLine(pen, r.X + 7 * s, r.Y + 8 * s, r.X + 7 * s, r.Y + _whyFull - 2 * s);
        using (var b = new SolidBrush(Palette.WithAlpha(Palette.TextDim, 0.4f + 0.6f * a)))
            g.DrawString(_model.Why, SmallFont, b, new RectangleF(r.X + 16 * s, r.Y + 8 * s, r.Width - 16 * s, _whyFull), Theme.Wrap);
        g.Restore(state);
    }

    private void DrawCheck(Graphics g, RectangleF r, (string label, bool done) item)
    {
        float s = _s;
        var c = new PointF(r.X + 9 * s, r.Y + 11 * s);
        if (item.done)
        {
            using (var b = new SolidBrush(Palette.Success)) g.FillEllipse(b, c.X - 9 * s, c.Y - 9 * s, 18 * s, 18 * s);
            Theme.IconCheck(g, c, 12 * s, Palette.Ink, 2.2f * s);
        }
        else using (var pen = new Pen(Palette.Line, 1.5f * s)) g.DrawEllipse(pen, c.X - 8 * s, c.Y - 8 * s, 16 * s, 16 * s);
        // the tick says "done"; the text stays readable (green text on dark reads worse, strike-through reads "disabled")
        using var tb = new SolidBrush(item.done ? Palette.TextDim : Palette.Text);
        g.DrawString(item.label, Theme.Font(14 * s), tb, new RectangleF(r.X + 26 * s, r.Y + 2 * s, r.Width - 26 * s, 22 * s), Theme.Wrap);
    }

    private void DrawNote(Graphics g, (string? title, string text, Color color, bool top) n, RectangleF r)
    {
        float s = _s, x = r.X, y = r.Y, w = r.Width, h = r.Height;
        using (var path = Theme.Rounded(r, Radius))
        {
            if (n.top) using (var b = new SolidBrush(Palette.WithAlpha(n.color, 0.06f))) g.FillPath(b, path);
            using var pen = new Pen(n.top ? Palette.WithAlpha(n.color, 0.55f) : Palette.Line, 1 * s);
            g.DrawPath(pen, path);
        }
        float tx = x + 34 * s, ty = y + 9 * s, tw = w - 44 * s;
        var ic = new PointF(x + 17 * s, y + 9 * s + 9 * s);
        if (Busy(_model) && !n.top)
        {
            float rr = 7 * s, ang = (Environment.TickCount % 1100) / 1100f * 360f;
            var c = new PointF(ic.X, y + h / 2);
            using (var track = new Pen(Palette.WithAlpha(n.color, 0.25f), 2.2f * s)) g.DrawEllipse(track, c.X - rr, c.Y - rr, 2 * rr, 2 * rr);
            using (var arc = new Pen(n.color, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(arc, c.X - rr, c.Y - rr, 2 * rr, 2 * rr, ang, 100);
        }
        else if (n.top) Theme.IconWarn(g, ic, 17 * s, n.color, 1.7f * s);
        else if (_model.Hint != null) Theme.IconMessage(g, ic, 16 * s, n.color, 1.7f * s);
        else using (var b = new SolidBrush(n.color)) g.FillEllipse(b, ic.X - 3.5f * s, ic.Y - 3.5f * s, 7 * s, 7 * s);
        if (n.title != null)
        {
            var tf = Theme.Font(14 * s, true);
            var th = Theme.Measure(n.title, tf, tw).Height;
            using var tb = new SolidBrush(Palette.Text);
            g.DrawString(n.title, tf, tb, new RectangleF(tx, ty, tw, th + 2), Theme.Wrap);
            ty += th + 2 * s;
        }
        using var db = new SolidBrush(Palette.Text);
        g.DrawString(n.text, SmallFont, db, new RectangleF(tx, ty, tw, r.Bottom - ty), Theme.Wrap);
    }

    /// <summary>The hotkeys; while the pointer is on an action, that action's name and key instead.</summary>
    private void DrawHint(Graphics g, RectangleF r)
    {
        var loc = Settings.Current.Text;
        string line;
        bool loud = false, dot = false;
        if (_flash != null) { line = _flash; loud = true; }
        else if (_hoverAgent && _model.Agent.Length > 0) { line = AgentStatus(); loud = true; dot = true; }
        else if (_hover is { Offline: true } ob) line = ob.Id == "outro_continue" ? loc.AgentOffContinue : loc.AgentOff;
        else if (_hover is { Custom: false, Enabled: true } hb && hb.Text.Length > 0 && hb.Action is not ("help_cancel" or "cant_find" or "dont_understand" or "help_other"))
        {
            var key = hb.Id == "help" ? "help" : hb.Action ?? "";
            line = Settings.Current.Hotkeys.TryGetValue(key, out var ch) ? $"{hb.Text} · {ch}" : hb.Text;
        }
        else line = _chatOpen ? loc.ChatHint : Settings.Current.HintLine(_model.State is "waiting" or "outro");
        if (line.Length == 0) return;
        if (dot)
        {
            // the state the icon stands for, in its colour, before the words
            float d = 7 * _s, cy = r.Y + HintFont.Height / 2f;
            using (var db = new SolidBrush(_model.Agent == "on" ? Palette.Success : Palette.TextDim)) g.FillEllipse(db, r.X, cy - d / 2, d, d);
            r = new RectangleF(r.X + d + 6 * _s, r.Y, r.Width - d - 6 * _s, r.Height);
        }
        using var b = new SolidBrush(loud ? Palette.Text : Palette.WithAlpha(Palette.TextDim, 0.85f));
        g.DrawString(line, HintFont, b, new RectangleF(r.X, r.Y, r.Width, r.Height + 2), Theme.Wrap);
    }

    /// <summary>What the assistant icon means now: here, or away (and for how long, once it is a minute or more).</summary>
    private string AgentStatus()
    {
        var loc = Settings.Current.Text;
        if (_model.Agent == "on") return loc.AgentOn;
        int min = _model.AgentSeen == default ? 0 : (int)(DateTime.UtcNow - _model.AgentSeen).TotalMinutes;
        return min >= 1 ? string.Format(loc.AgentOffFor, min) : loc.AgentOff;
    }

    private void DrawButton(Graphics g, Btn b)
    {
        float s = _s;
        bool hover = _hover == b && b.Enabled && !b.Offline;
        var r = b.Rect;
        if (b.IconOnly)
        {
            if (hover) using (var br = new SolidBrush(Palette.InkRaised)) g.FillEllipse(br, r);
            DrawIcon(g, b.Icon, new PointF(r.X + r.Width / 2, r.Y + r.Height / 2), hover ? Palette.Text : Palette.TextDim);
            return;
        }
        bool lit = (b.Enabled && !b.Offline) || b.Status;
        var fg = Theme.OutlineButton(g, r, b.Style, hover, lit, s);
        if (b.Offline)
        {
            // the struck-through assistant at the end says why it is dimmed; the caption keeps the rest of the room
            Theme.IconAgent(g, new PointF(r.Right - 18 * s, r.Y + r.Height / 2 + 1 * s), 15 * s, Palette.WithAlpha(Palette.TextDim, 0.7f), 1.5f * s, true);
            r = new RectangleF(r.X, r.Y, r.Width - 22 * s, r.Height);
        }
        var f = Theme.Font(13.5f * s, true);
        float iconW = b.Icon.Length > 0 ? 18 * s : 0;
        float textW = b.Text.Length > 0 ? Math.Min(Theme.Measure(b.Text, f, r.Width).Width, r.Width - iconW - (iconW > 0 ? 8 * s : 0) - (b.Left ? 28 : 16) * s) : 0;
        float total = iconW + (textW > 0 && iconW > 0 ? 8 * s : 0) + textW;
        float x = b.Left ? r.X + 14 * s : r.X + (r.Width - total) / 2, cy = r.Y + r.Height / 2;
        bool iconAfter = b.Icon == "check";
        float ix = iconAfter ? x + textW + 8 * s : x;
        float tx = iconAfter ? x : x + iconW + (iconW > 0 ? 8 * s : 0);
        var iconColor = b.Icon is "help" or "search" or "bulb" or "message" && lit ? Palette.Named["blue"] : fg;
        if (iconW > 0) DrawIcon(g, b.Icon, new PointF(ix + iconW / 2, cy), iconColor);
        if (textW > 0)
            using (var tb = new SolidBrush(fg))
            using (var fmt = new StringFormat(Theme.Center) { Alignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(b.Text, f, tb, new RectangleF(tx, r.Y, textW + 2, r.Height), fmt);
    }

    private void DrawIcon(Graphics g, string icon, PointF c, Color color)
    {
        float s = _s;
        switch (icon)
        {
            case "back": Theme.IconChevron(g, c, 14 * s, true, color, 2.1f * s); break;
            case "undo": Theme.IconUndo(g, c, 19 * s, color, 2f * s); break;
            case "skip": Theme.IconSkip(g, c, 12 * s, color, 2f * s); break;
            case "check": Theme.IconCheck(g, c, 13 * s, color, 2.3f * s); break;
            case "help": Theme.IconQuestion(g, c, 16 * s, color, 1.9f * s); break;
            case "search": Theme.IconSearch(g, c, 16 * s, color, 1.9f * s); break;
            case "bulb": Theme.IconBulb(g, c, 16 * s, color, 1.8f * s); break;
            case "message": Theme.IconMessage(g, c, 16 * s, color, 1.8f * s); break;
        }
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is Point from && e.Button == MouseButtons.Left)
        {
            var cur = Cursor.Position;
            // the screen's edge and the taskbar stop the card itself: it slides along them, whole and still
            var at = DragTo(new Point(_dragOrigin.X + cur.X - from.X, _dragOrigin.Y + cur.Y - from.Y), Size, Native.WorkAreaAt(new PointF(cur.X, cur.Y)));
            MoveTo(new Rectangle(at, Size));
            _pinned = true;
            _pinnedStepVersion = _model.StepVersion;
            return;
        }
        var hover = _buttons.FirstOrDefault(b => b.Rect.Contains(e.Location));
        bool hc = _closeRect.Contains(e.Location), hw = _whyRect.Contains(e.Location), ha = _agentRect.Contains(e.Location);
        bool hx = _backRect.Contains(e.Location) || _chatRowRect.Contains(e.Location) || (_composeRect.Contains(e.Location) && _model.Agent != "off");
        if (hover != _hover || hc != _hoverClose || hw != _hoverWhy || ha != _hoverAgent || hx != _hoverChat)
        {
            _hover = hover; _hoverClose = hc; _hoverWhy = hw; _hoverAgent = ha; _hoverChat = hx;
            Cursor = hover is { Enabled: true, Offline: false } || hc || hw || hx ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null || _hoverClose || _hoverWhy || _hoverAgent) { _hover = null; _hoverClose = false; _hoverWhy = false; _hoverAgent = false; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = _buttons.FirstOrDefault(b => b.Rect.Contains(e.Location) && b.Enabled);
        if (_pressed == null && !_closeRect.Contains(e.Location) && !_whyRect.Contains(e.Location)
            && !_backRect.Contains(e.Location) && !_chatRowRect.Contains(e.Location) && !_composeRect.Contains(e.Location))
        {
            _dragFrom = Cursor.Position;
            _dragOrigin = Location;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool dragged = _dragFrom != null && Location != _dragOrigin;
        _dragFrom = null;
        if (e.Button != MouseButtons.Left || dragged) return;
        if (_closeRect.Contains(e.Location)) { Engine.UserAction("stop", "card"); return; }
        if (_backRect.Contains(e.Location)) { BackToStep(); return; }
        if (_chatRowRect.Contains(e.Location)) { OpenChat(false); return; }
        if (_composeRect.Contains(e.Location))
        {
            // the pencil drawn in the line goes straight to marking the screen
            bool pencil = e.X >= _composeRect.Right - 6 * _s - 28 * _s - 4 * _s - 28 * _s && e.X < _composeRect.Right - 6 * _s - 28 * _s;
            if (_model.Agent == "off") Flash(Settings.Current.Text.AgentOff);
            else OpenCompose(pencil);
            return;
        }
        if (_whyRect.Contains(e.Location))
        {
            _whyOpen = !_whyOpen;
            _whyTick = Environment.TickCount64;
            Relayout();
            return;
        }
        var b = _buttons.FirstOrDefault(x => x.Rect.Contains(e.Location) && x.Enabled);
        if (b == null || b != _pressed) return;
        _pressed = null;
        if (b.Offline)
        {
            // nobody would answer: say where the answer comes from instead of sending the press into the void
            var loc = Settings.Current.Text;
            Flash(b.Id == "outro_continue" ? loc.AgentOffContinue : loc.AgentOff);
            return;
        }
        if (b.Custom) Engine.ButtonPressed("card", b.Id, b.Action, "card");
        else if (b.Action == "help_open") { _helpOpen = true; _helpStepVersion = _model.StepVersion; Relayout(); }
        else if (b.Action == "help_cancel") { _helpOpen = false; Relayout(); }
        else if (b.Action == "help_other") OpenChat(true);
        else
        {
            if (b.Action is "cant_find" or "dont_understand") _helpOpen = false;
            Engine.UserAction(b.Action!, "card");
        }
    }

    // ------------------------------------------------------------------ the step's conversation

    private void Flash(string text, int ms = FlashMs)
    {
        _flash = text;
        _flashUntil = Environment.TickCount64 + ms;
        Invalidate();
    }

    /// <summary>Shows the conversation instead of the step; with <paramref name="compose"/>, the line to type in too.</summary>
    private void OpenChat(bool compose)
    {
        _helpOpen = false;
        _chatOpen = true;
        _seen[_model.Index] = _model.Chat.Count;
        Relayout();
        if (compose && _model.Agent != "off") OpenCompose();
    }

    /// <summary>Back to the step: the conversation folds into its row; what was typed stays for later.</summary>
    private void BackToStep()
    {
        _chatOpen = false;
        CloseCompose();
        Relayout();
    }

    private void CloseChat(bool discard)
    {
        _chatOpen = false;
        CloseCompose();
        if (!discard) return;
        _draft = "";
        _draftSketch?.Dispose();
        _draftSketch = null;
    }

    private void CloseCompose()
    {
        if (_compose == null) return;
        var c = _compose;
        _compose = null;
        c.Dismiss();
    }

    private Rectangle ComposeScreenRect() => new(
        Left + (int)Math.Round(_composeRect.X), Top + (int)Math.Round(_composeRect.Y),
        (int)Math.Round(_composeRect.Width), (int)Math.Round(_composeRect.Height));

    /// <summary>
    /// The card never takes the keyboard (the app keeps it), so typing happens in a small window laid exactly over
    /// the card's input line; it follows the card and hands what was typed back when the user clicks away.
    /// </summary>
    private void OpenCompose(bool mark = false)
    {
        if (_compose != null) { _compose.Grab(); if (mark) _compose.StartSketch(); return; }
        if (!_chatOpen) { _chatOpen = true; Relayout(); }
        if (_composeRect.IsEmpty) return;
        var loc = Settings.Current.Text;
        var sketch = _draftSketch;
        _draftSketch = null;                                   // the box owns it now
        _compose = new ComposeBox(ComposeScreenRect(), _s, _draft, sketch, _model.Chat.Count > 0 ? loc.ChatReply : loc.ChatAsk,
            (text, sk) =>
            {
                _draft = "";
                // the marked area is described via UI Automation: keep that off the UI thread
                if (sk == null) Engine.RequestHelp("other", "card", text);
                else System.Threading.Tasks.Task.Run(() => Engine.RequestHelp("other", "card", text, sk));
            },
            (box, text, sk, back) =>
            {
                if (_compose == box) _compose = null;
                _draft = text;
                _draftSketch?.Dispose();
                _draftSketch = sk;
                if (back) _chatOpen = false;
                if (!IsDisposed) Relayout();
            });
        // owned by the card: it stays above it however often the card puts itself on top again
        _compose.Show(this);
        _compose.Grab();
        if (mark) _compose.StartSketch();
    }

    private Bitmap? Thumb(string? path)
    {
        if (path == null) return null;
        if (_thumbs.TryGetValue(path, out var b)) return b;
        Bitmap? t = null;
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            using var img = Image.FromStream(fs);
            float k = Math.Min(1f, Math.Min(264f / img.Width, 160f / img.Height));
            t = new Bitmap(img, Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
        }
        catch (Exception e) { Log.Debug("chat picture: " + e.Message); }
        _thumbs[path] = t;
        return t;
    }

    /// <summary>
    /// Lays the conversation out as bubbles (the user's on the right, the agent's on the left) and keeps the newest
    /// that fit; returns the height. An empty one starts with the card's own question.
    /// </summary>
    private float LayoutThread(float w)
    {
        var m = _model;
        var loc = Settings.Current.Text;
        float s = _s, maxW = w * 0.86f, padX = 11 * s, padY = 8 * s, gap = 8 * s, inner = maxW - 2 * padX;
        var all = new List<Bubble>();
        if (m.Chat.Count == 0) all.Add(new Bubble { Line = new ChatLine("agent", loc.ChatHello) });
        all.AddRange(m.Chat.Select(l => new Bubble { Line = l }));
        if (m.HelpPending && (m.Chat.Count == 0 || m.Chat[^1].Who != "agent")) all.Add(new Bubble { Typing = true });
        var tf = Theme.Font(13.5f * s, true);
        foreach (var b in all)
        {
            if (b.Typing) { b.LabelH = 18 * s; b.R = new RectangleF(0, 0, 96 * s, 2 * padY + b.LabelH + 14 * s); continue; }
            var l = b.Line!;
            float cw = 0;
            if (l.Who == "agent")
            {
                b.LabelH = 18 * s;
                cw = Math.Max(cw, 20 * s + Theme.Measure(loc.ChatAgent, Theme.Font(11.5f * s, true), inner).Width);
            }
            if (l.Title != null)
            {
                var sz = Theme.Measure(l.Title, tf, inner);
                b.TitleH = sz.Height + 2 * s;
                cw = Math.Max(cw, sz.Width);
            }
            if (l.Text.Length > 0)
            {
                var sz = Theme.Measure(l.Text, SmallFont, inner);
                b.TextH = sz.Height;
                cw = Math.Max(cw, sz.Width);
            }
            if (Thumb(l.Image) is Bitmap img)
            {
                float k = Math.Min(inner / img.Width, 80 * s / img.Height);
                b.ImgH = img.Height * k + (b.TextH > 0 ? 6 * s : 0);
                cw = Math.Max(cw, img.Width * k);
            }
            b.R = new RectangleF(0, 0, Math.Min(maxW, cw + 2 * padX + 2 * s), 2 * padY + b.LabelH + b.TitleH + b.TextH + b.ImgH);
        }
        // the newest that fit; older ones scroll away (the agent has them all)
        float limit = 330 * s, total = 0;
        int first = all.Count;
        while (first > 0)
        {
            float h = all[first - 1].R.Height + (first < all.Count ? gap : 0);
            if (total + h > limit && first < all.Count) break;
            total += h;
            first--;
        }
        _bubbles.Clear();
        float y = 0;
        for (int i = first; i < all.Count; i++)
        {
            var b = all[i];
            bool right = b.Line?.FromUser == true;
            b.R = new RectangleF(right ? w - b.R.Width : 0, y, b.R.Width, b.R.Height);
            y += b.R.Height + gap;
            _bubbles.Add(b);
        }
        return Math.Max(0, y - gap);
    }

    private void DrawThread(Graphics g, RectangleF r)
    {
        var loc = Settings.Current.Text;
        float s = _s, padX = 11 * s, padY = 8 * s;
        var blue = Palette.Named["blue"];
        foreach (var b in _bubbles)
        {
            var rr = new RectangleF(r.X + b.R.X, r.Y + b.R.Y, b.R.Width, b.R.Height);
            bool user = b.Line?.FromUser == true, note = b.Line?.Who == "note";
            using (var path = Theme.Rounded(rr, 9 * s))
            {
                if (user) using (var f = new SolidBrush(Palette.InkRaised)) g.FillPath(f, path);
                using var pen = new Pen(user || note ? Palette.Line : Palette.WithAlpha(blue, 0.55f), 1 * s);
                if (note) pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, path);
            }
            float x = rr.X + padX, y = rr.Y + padY, iw = rr.Width - 2 * padX;
            if (b.LabelH > 0)
            {
                Theme.IconAgent(g, new PointF(x + 7 * s, y + 9 * s), 13 * s, blue, 1.4f * s, false);
                using var lb = new SolidBrush(blue);
                g.DrawString(loc.ChatAgent, Theme.Font(11.5f * s, true), lb, x + 18 * s, y + 1 * s);
                y += b.LabelH;
            }
            if (b.Typing)
            {
                // three dots that take turns, while the agent works on the answer
                int phase = (int)(Environment.TickCount64 / 300 % 3);
                for (int i = 0; i < 3; i++)
                    using (var db = new SolidBrush(Palette.WithAlpha(blue, i == phase ? 1f : 0.35f)))
                        g.FillEllipse(db, x + 2 * s + i * 11 * s, y + 4 * s, 6 * s, 6 * s);
                continue;
            }
            var l = b.Line!;
            if (l.Title != null)
            {
                using var tb = new SolidBrush(Palette.Text);
                g.DrawString(l.Title, Theme.Font(13.5f * s, true), tb, new RectangleF(x, y, iw + 2 * s, b.TitleH + 2), Theme.Wrap);
                y += b.TitleH;
            }
            if (l.Text.Length > 0)
            {
                using var tb = new SolidBrush(note ? Palette.TextDim : Palette.Text);
                g.DrawString(l.Text, SmallFont, tb, new RectangleF(x, y, iw + 2 * s, b.TextH + 2), Theme.Wrap);
                y += b.TextH;
            }
            if (b.ImgH > 0 && Thumb(l.Image) is Bitmap img)
            {
                if (b.TextH > 0) y += 6 * s;
                float k = Math.Min(iw / img.Width, 80 * s / img.Height);
                var ir = new RectangleF(x, y, img.Width * k, img.Height * k);
                var state = g.Save();
                using (var clip = Theme.Rounded(ir, 5 * s)) g.SetClip(clip);
                g.DrawImage(img, ir);
                g.Restore(state);
                using var path = Theme.Rounded(ir, 5 * s);
                using var pen = new Pen(Palette.Line, 1 * s);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>The input line as the card draws it; while typing, the compose window covers it exactly.</summary>
    private void DrawCompose(Graphics g, RectangleF r)
    {
        var m = _model;
        var loc = Settings.Current.Text;
        float s = _s;
        bool off = m.Agent == "off";
        using (var path = Theme.Rounded(r, 7 * s))
        using (var pen = new Pen(_hoverChat && !off ? Palette.TextDim : Palette.Line, 1 * s))
            g.DrawPath(pen, path);
        float bx = r.Right - 6 * s - 28 * s, by = r.Y + (r.Height - 28 * s) / 2;
        var send = new RectangleF(bx, by, 28 * s, 28 * s);
        Theme.OutlineButton(g, send, "primary", false, !off, s);
        DrawSendArrow(g, new PointF(send.X + 14 * s, send.Y + 14 * s), Palette.WithAlpha(Palette.Accent, off ? 0.45f : 1f), s);
        Theme.IconPencil(g, new PointF(bx - 4 * s - 14 * s, r.Y + r.Height / 2), 16 * s, Palette.WithAlpha(Palette.TextDim, off ? 0.45f : 1f), 1.6f * s);
        float tx = r.X + 12 * s;
        if (_draftSketch is { Empty: false } d)
        {
            // what was marked and not sent yet: the pencil and the number of shapes
            Theme.IconPencil(g, new PointF(tx + 7 * s, r.Y + r.Height / 2), 14 * s, Palette.Accent, 1.6f * s);
            using var nb = new SolidBrush(Palette.Accent);
            g.DrawString(d.Strokes.Count.ToString(), Theme.Font(12 * s, true), nb, tx + 16 * s, r.Y + r.Height / 2 - 8 * s);
            tx += 34 * s;
        }
        string text = off ? loc.AgentOff : _draft.Length > 0 ? _draft : m.Chat.Count > 0 ? loc.ChatReply : loc.ChatAsk;
        var color = off ? Palette.WithAlpha(Palette.TextDim, 0.7f) : _draft.Length > 0 ? Palette.Text : Palette.WithAlpha(Palette.TextDim, 0.8f);
        using var tb = new SolidBrush(color);
        using var fmt = new StringFormat(Theme.Center) { Alignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, Theme.Font(13.5f * s), tb, new RectangleF(tx, r.Y, bx - 30 * s - tx, r.Height), fmt);
    }

    internal static void DrawSendArrow(Graphics g, PointF c, Color color, float s)
    {
        using var pen = new Pen(color, 1.9f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(pen, c.X, c.Y + 6 * s, c.X, c.Y - 6 * s);
        g.DrawLines(pen, new[] { new PointF(c.X - 5 * s, c.Y - 1 * s), new PointF(c.X, c.Y - 6 * s), new PointF(c.X + 5 * s, c.Y - 1 * s) });
    }

    /// <summary>The folded conversation under the step's text: its name, the newest message, a dot for one unseen.</summary>
    private void DrawChatRow(Graphics g, RectangleF r)
    {
        var m = _model;
        var loc = Settings.Current.Text;
        float s = _s, cy = r.Y + r.Height / 2;
        var blue = Palette.Named["blue"];
        using (var path = Theme.Rounded(r, 6 * s))
        using (var pen = new Pen(_hoverChat ? Palette.TextDim : Palette.Line, 1 * s))
            g.DrawPath(pen, path);
        Theme.IconMessage(g, new PointF(r.X + 15 * s, cy + 1 * s), 15 * s, blue, 1.6f * s);
        var lf = Theme.Font(13 * s, true);
        float lw = Theme.Measure(loc.ChatRow, lf, 300 * s).Width;
        using (var lb = new SolidBrush(blue)) g.DrawString(loc.ChatRow, lf, lb, r.X + 28 * s, cy - lf.Height / 2f);
        bool unseen = m.Chat.Count > ChatSeen;
        float right = r.Right - 22 * s - (unseen ? 14 * s : 0);
        var last = m.Chat[^1];
        string preview = m.HelpPending && last.FromUser ? loc.ChatLooking : last.Title ?? last.Text;
        float px = r.X + 28 * s + lw + 8 * s;
        if (right - px > 30 * s)
        {
            using var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
            using var pb = new SolidBrush(Palette.WithAlpha(Palette.TextDim, 0.85f));
            g.DrawString(preview.Replace('\n', ' '), SmallFont, pb, new RectangleF(px, r.Y, right - px, r.Height), fmt);
        }
        if (unseen) using (var db = new SolidBrush(Palette.Accent)) g.FillEllipse(db, r.Right - 32 * s, cy - 4 * s, 8 * s, 8 * s);
        Theme.IconChevron(g, new PointF(r.Right - 11 * s, cy), 10 * s, false, _hoverChat ? Palette.Text : Palette.TextDim, 1.8f * s);
    }

    /// <summary>In the conversation the header's left side leads back to the step: "‹ Step 3 · its title".</summary>
    private void DrawBack(Graphics g, RectangleF r, float edge)
    {
        var m = _model;
        var loc = Settings.Current.Text;
        float s = _s, cy = r.Y + r.Height / 2;
        var c = _hoverChat && _backRect.Contains(PointToClient(Cursor.Position)) ? Palette.Text : Palette.TextDim;
        Theme.IconChevron(g, new PointF(r.X + 5 * s, cy), 11 * s, true, c, 2 * s);
        var step = string.Format(loc.ChatBack, m.Index + 1);
        var fa = HeadFont(true);
        float x = r.X + 16 * s, aw = Theme.Measure(step, fa, 300 * s).Width;
        using (var b = new SolidBrush(Palette.Text)) g.DrawString(step, fa, b, x, cy - fa.Height / 2f);
        float tx = x + aw + 2 * s, tw = edge - 8 * s - tx;
        if (tw > 20 * s && m.Title.Length > 0)
        {
            using var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
            using var b = new SolidBrush(Palette.TextDim);
            g.DrawString("· " + m.Title, HeadFont(false), b, new RectangleF(tx, r.Y, tw, r.Height), fmt);
            tx += Math.Min(tw, Theme.Measure("· " + m.Title, HeadFont(false), 600 * s).Width);
        }
        _backRect = new RectangleF(r.X, r.Y, tx - r.X, r.Height);
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

    /// <summary>The on-screen buttons' windows.</summary>
    public IEnumerable<IntPtr> Handles => _windows.Values.Select(w => w.Handle);

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
/// The line the user types a question into, laid exactly over the card's input line. The card can't take keyboard
/// input (it never activates, so the app keeps focus) — this window does, briefly. Enter sends and the line stays for
/// the next question; Esc goes back to the step; clicking elsewhere closes it and the card keeps what was typed.
/// </summary>
internal sealed class ComposeBox : Form
{
    private readonly TextBox _box;
    private readonly IntPtr _returnTo;
    private readonly float _s;
    private Sketch _sketch;
    private readonly Action<string, Sketch?> _send;
    private readonly Action<ComposeBox, string, Sketch?, bool> _closed;
    private bool _sketching, _done;
    private RectangleF _thumbRect, _thumbX, _pencilRect, _sendRect;
    private string _hover = "";
    private Bitmap? _thumb;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    /// <param name="send">a question to send (the sketch, if any, now belongs to the receiver)</param>
    /// <param name="closed">the box closed: what was typed and marked and not sent, and whether to go back to the step</param>
    public ComposeBox(Rectangle at, float s, string draft, Sketch? sketch, string placeholder,
        Action<string, Sketch?> send, Action<ComposeBox, string, Sketch?, bool> closed)
    {
        _s = s;
        _send = send;
        _closed = closed;
        _sketch = sketch ?? new Sketch();
        _returnTo = GetForegroundWindow();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Palette.Ink;
        KeyPreview = true;
        Text = "TutorialOverlay question";
        Bounds = at;
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Palette.Ink, ForeColor = Palette.Text,
            Font = Theme.Font(13.5f * s), PlaceholderText = placeholder, Text = draft,
        };
        Controls.Add(_box);
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == System.Windows.Forms.Keys.Enter) { e.SuppressKeyPress = true; Submit(); }
            else if (e.KeyCode == System.Windows.Forms.Keys.Escape) { e.SuppressKeyPress = true; Finish(true); }
        };
        Relayout();
        _box.SelectionStart = _box.TextLength;
    }

    /// <summary>The card moved or grew: stay over its input line.</summary>
    public void Follow(Rectangle r)
    {
        if (_done) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (Bounds == r) return;
        Bounds = r;
        Relayout();
        Invalidate();
    }

    /// <summary>Takes the keyboard (the card was clicked, so this process may).</summary>
    public void Grab()
    {
        if (_sketching || _done) return;
        Activate();
        SetForegroundWindow(Handle);
        _box.Focus();
    }

    /// <summary>Closed by the card (its step changed, it hid): what was typed goes back to it.</summary>
    public void Dismiss() => Finish(false, false);

    private long _shownAt;
    private int _regrabs;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shownAt = Environment.TickCount64;
        _box.Focus();
    }

    private void Relayout()
    {
        float s = _s, w = ClientSize.Width, h = ClientSize.Height, b = 28 * s;
        _sendRect = new RectangleF(w - 6 * s - b, (h - b) / 2, b, b);
        _pencilRect = new RectangleF(_sendRect.X - 4 * s - b, (h - b) / 2, b, b);
        float x = 12 * s;
        _thumb?.Dispose();
        _thumb = null;
        _thumbRect = _thumbX = RectangleF.Empty;
        if (!_sketch.Empty)
        {
            // what was marked, as a small picture with a cross to drop it (a click on it draws again)
            _thumbRect = new RectangleF(6 * s, 5 * s, 50 * s, h - 10 * s);
            _thumbX = new RectangleF(_thumbRect.Right - 13 * s, _thumbRect.Y - 1 * s, 14 * s, 14 * s);
            _thumb = _sketch.Render();
            x = _thumbRect.Right + 8 * s;
        }
        int th = _box.PreferredHeight;
        _box.Bounds = Rectangle.Round(new RectangleF(x, (h - th) / 2f, Math.Max(10, _pencilRect.X - 6 * s - x), th));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 3;      // small rounding, like the card's input line under it
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!Visible || _sketching || _done) return;
        var fg = GetForegroundWindow();
        string who;
        try { who = System.Diagnostics.Process.GetProcessById(Native.ProcessId(fg)).ProcessName; } catch { who = "?"; }
        // just opened, and another window took the keyboard at once (the app the click came from, re-asserting its
        // focus): that is not the user clicking away — take it back, a few times at most
        if (Environment.TickCount64 - _shownAt < 600 && _regrabs < 3)
        {
            _regrabs++;
            Log.Info($"question box: focus taken by {who} right after opening, taking it back");
            BeginInvoke(new Action(Grab));
            return;
        }
        Log.Info($"question box: closed, focus went to {who}");
        // clicking elsewhere: the box goes, the card keeps the draft
        BeginInvoke(new Action(() => Finish(false, false)));
    }

    private void Submit()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0 && _sketch.Empty) { _box.Focus(); return; }
        if (text.Length == 0) text = Settings.Current.Text.ChatSketchOnly;
        var sk = _sketch.Empty ? null : _sketch;
        if (sk == null) _sketch.Dispose();
        _sketch = new Sketch();
        _box.Text = "";
        Relayout();
        Invalidate();
        _send(text, sk);
    }

    private void Finish(bool back, bool returnFocus = true)
    {
        if (_done) return;
        _done = true;
        var keep = _sketch.Empty ? null : _sketch;
        if (keep == null) _sketch.Dispose();
        var r = _returnTo;
        _closed(this, _box.Text, keep, back);
        Close();
        if (back && returnFocus && r != IntPtr.Zero) SetForegroundWindow(r);
    }

    /// <summary>Pencil: hide, let the user mark the area on a frozen screen, come back with the text intact.</summary>
    public void StartSketch()
    {
        if (_sketching || _done) return;
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
                Relayout();
                Invalidate();
            });
        };
        t.Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hv = _thumbX.Contains(e.Location) ? "thumbx" : _thumbRect.Contains(e.Location) ? "thumb"
            : _pencilRect.Contains(e.Location) ? "pencil" : _sendRect.Contains(e.Location) ? "send" : "";
        if (hv != _hover) { _hover = hv; Cursor = hv.Length > 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        switch (_hover)
        {
            case "thumbx": _sketch.Strokes.Clear(); Relayout(); Invalidate(); _box.Focus(); break;
            case "thumb" or "pencil": StartSketch(); break;
            case "send": Submit(); break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Quality(g);
        float s = _s, w = ClientSize.Width, h = ClientSize.Height;
        g.Clear(Palette.Ink);
        using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 7 * s))
        using (var pen = new Pen(Palette.Accent, 1.2f * s))
            g.DrawPath(pen, path);
        if (_thumb != null)
        {
            var st = g.Save();
            using (var clip = Theme.Rounded(_thumbRect, 4 * s)) g.SetClip(clip);
            // cover the box: the middle of the picture, where the marks are
            float k = Math.Max(_thumbRect.Width / _thumb.Width, _thumbRect.Height / _thumb.Height);
            float iw = _thumb.Width * k, ih = _thumb.Height * k;
            g.DrawImage(_thumb, _thumbRect.X + (_thumbRect.Width - iw) / 2, _thumbRect.Y + (_thumbRect.Height - ih) / 2, iw, ih);
            g.Restore(st);
            using (var path = Theme.Rounded(_thumbRect, 4 * s))
            using (var pen = new Pen(_hover == "thumb" ? Palette.TextDim : Palette.Line, 1 * s)) g.DrawPath(pen, path);
            using (var xb = new SolidBrush(Palette.WithAlpha(Palette.Ink, 0.9f))) g.FillEllipse(xb, _thumbX);
            Theme.IconCross(g, new PointF(_thumbX.X + _thumbX.Width / 2, _thumbX.Y + _thumbX.Height / 2), 7 * s, _hover == "thumbx" ? Palette.Text : Palette.TextDim, 1.5f * s);
        }
        var pc = _hover == "pencil" ? Palette.Text : _sketch.Empty ? Palette.TextDim : Palette.Accent;
        if (_hover == "pencil") using (var hb = new SolidBrush(Palette.InkRaised)) using (var p = Theme.Rounded(_pencilRect, 6 * s)) g.FillPath(hb, p);
        Theme.IconPencil(g, new PointF(_pencilRect.X + _pencilRect.Width / 2, _pencilRect.Y + _pencilRect.Height / 2), 16 * s, pc, 1.6f * s);
        Theme.OutlineButton(g, _sendRect, "primary", _hover == "send", true, s);
        CardWindow.DrawSendArrow(g, new PointF(_sendRect.X + _sendRect.Width / 2, _sendRect.Y + _sendRect.Height / 2), Palette.Accent, s);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _thumb?.Dispose();
        base.Dispose(disposing);
    }
}
