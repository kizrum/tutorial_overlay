using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>What the mascot needs from the engine for one frame (built under the engine lock).</summary>
internal sealed class MascotInput
{
    public PointF? Target;                 // the spot to point at; null = stay where it is
    public RectangleF TargetBounds;        // never cover it
    public IntPtr Root;                    // the target's window: its own pixels tell busy UI from empty space
    public string? Say;                    // speech bubble
    public float Progress;                 // 0..1, the bar on its forehead
    public bool Thinking, Outro, Waiting;
    public string StepKey = "";            // a new step re-shows the bubble and may move it
    public List<RectangleF> Avoid = new(); // marks, captions, popups
    public float Scale = 1;
}

/// <summary>
/// "Шажок": the step card come alive. Stands next to the current target, points at it and says the step's line;
/// hops on a hit, shakes its head on a miss, thinks while the agent prepares a hint, celebrates at the end.
/// Moving to the next target: if its hand already reaches, it only turns the arm; if the new spot is a few steps
/// away it walks there; farther, it vanishes and pops up at the new spot — no long trips across the screen.
/// Everything is decided here from screen positions and engine events — the agent never drives it, so its
/// motion costs no tokens. Drawn in code from the design sheet's shapes (300×260 design units), in its own small
/// click-through layered window, so animating it never redraws the marks' canvas.
/// </summary>
internal sealed class MascotWindow : Form
{
    // ---- design palette
    private static readonly Color Body = Color.FromArgb(255, 27, 30, 39), Edge = Color.FromArgb(255, 58, 62, 76),
        Limb = Color.FromArgb(255, 201, 206, 219), White = Color.FromArgb(255, 240, 242, 247), Pupil = Color.FromArgb(255, 18, 20, 26),
        Track = Color.FromArgb(255, 42, 46, 58);

    private const float Unit = 0.44f;            // design unit → px at 100 %: the body (136 u) is ~60 px
    private const float WalkSpeed = 240;         // px/s at 100 %: slow enough to see the steps, a short hop still takes 0.4 s
    private const float WalkFigures = 3.2f;      // walk only up to this many figure widths; farther: vanish + appear
    private const float VanishTime = 0.2f, AppearTime = 0.3f;

    private enum Mood { Normal, Happy, Miss }
    private enum Motion { None, Walk, Vanish, Appear }

    private readonly ConcurrentQueue<string> _events = new();
    private DateTime _last = DateTime.UtcNow;
    private float _time;

    // where it is and where it goes (screen px, centre of the figure)
    private bool _placed;
    private PointF _pos, _from, _to;
    private PointF _offset;                      // spot relative to the target: follows it when the window moves
    private Motion _move;
    private float _moveT, _moveDur;
    private float _walkPhase;
    private bool _flip, _faceLeft;
    private float _alpha;
    private string _spotKey = "";                // the step whose spot was chosen
    private PointF _spotTarget;

    private Mood _mood;
    private float _moodLeft;
    private float _nextBlink = 3, _blink;
    private string _stepKey = "";
    private float _bubble;                       // 0..1 fade
    private bool _visible;
    private float _raisedAt;
    private MascotInput? _lastInput;

    public MascotWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "TutorialOverlay mascot";
        Bounds = new Rectangle(0, 0, 1, 1);
        TutorialOverlay.Events.Published += ev => { if (ev["event"]?.GetValue<string>() is string kind) _events.Enqueue(kind); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = Native.WS_POPUP;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Settings.Global.HideFromCapture) Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    // =========================================================== per frame (UI thread)

    public void Update(MascotInput? inp)
    {
        var now = DateTime.UtcNow;
        float dt = (float)Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
        _last = now;
        _time += dt;

        while (_events.TryDequeue(out var kind))
        {
            if (kind == "hit") { _mood = Mood.Happy; _moodLeft = 0.9f; }
            else if (kind == "miss" && _mood != Mood.Happy) { _mood = Mood.Miss; _moodLeft = 1.1f; }
            else if (kind is "tutorial_started" or "tutorial_stopped") { _placed = false; _spotKey = ""; }
        }
        if (_mood != Mood.Normal && (_moodLeft -= dt) <= 0) _mood = Mood.Normal;

        _alpha = Approach(_alpha, inp != null ? 1 : 0, dt * 6);
        if (inp == null)
        {
            if (_alpha <= 0.01f) { Hide(); _placed = false; _spotKey = ""; }
            else Render(_lastInput, dt);
            return;
        }
        _lastInput = inp;

        Steer(inp);
        Advance(dt, inp.Scale);

        if ((_nextBlink -= dt) <= 0) { _blink = 0.14f; _nextBlink = 2.5f + (float)Random.Shared.NextDouble() * 3; }
        if (_blink > 0) _blink -= dt;

        if (inp.StepKey != _stepKey) { _stepKey = inp.StepKey; _bubble = 0; }
        bool settled = _move == Motion.None && _alpha > 0.98f;
        bool wantBubble = inp.Say is { Length: > 0 } && settled && !inp.Thinking && _mood != Mood.Miss && !inp.Outro;
        _bubble = Approach(_bubble, wantBubble ? 1 : 0, dt * 5);

        // tell the engine the highlight can come in: standing still, fully shown, at this step's spot
        Engine.MascotSettledKey = settled && _spotKey == inp.StepKey ? inp.StepKey : null;
        Render(inp, dt);
    }

    private static float Approach(float v, float target, float step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);
    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static float SmoothStep(float t) => t * t * (3 - 2 * t);

    // =========================================================== moving

    /// <summary>Decide where to be for this frame's target, and how to get there.</summary>
    private void Steer(MascotInput inp)
    {
        float s = inp.Scale;
        var size = FigureSize(s);
        if (inp.Target is not PointF t)
        {
            // nothing to point at: stay; the very first time, stand by the card
            if (!_placed)
            {
                var c = Engine.CardRect is Rectangle cr && cr.Width > 0 ? cr : Screen.PrimaryScreen!.WorkingArea;
                StartAppear(new PointF(c.Left - size.Width * 0.6f, c.Bottom - size.Height * 0.6f));
                _spotKey = inp.StepKey;
            }
            else if (_spotKey != inp.StepKey && _move == Motion.None) _spotKey = inp.StepKey;
            return;
        }

        // a hit first plays its hop; a move already under way finishes first
        if (_mood == Mood.Happy || _move != Motion.None) return;

        if (!_placed)
        {
            var spot = ChooseSpot(inp, t, size, s);
            StartAppear(spot);
            _offset = new PointF(spot.X - t.X, spot.Y - t.Y);
            _spotKey = inp.StepKey;
            _spotTarget = t;
            return;
        }

        if (_spotKey == inp.StepKey)
        {
            // same step: ride along with the target (window dragged, content scrolled)
            var glued = new PointF(t.X + _offset.X, t.Y + _offset.Y);
            if (Dist(t, _spotTarget) > 0.5f)
            {
                _spotTarget = t;
                if (SpotOk(inp, glued, size, s)) _pos = glued;
                else _spotKey = "";      // the spot went bad (off screen, now covering something): choose again
            }
            if (_spotKey == inp.StepKey) return;
        }

        // a new step (or a spot that went bad)
        _spotKey = inp.StepKey;
        _spotTarget = t;
        if (Reaches(inp, t, size, s) && SpotOk(inp, _pos, size, s))
        {
            // the hand reaches from here: just point the other way
            _offset = new PointF(_pos.X - t.X, _pos.Y - t.Y);
            return;
        }
        var dest = ChooseSpot(inp, t, size, s);
        _offset = new PointF(dest.X - t.X, dest.Y - t.Y);
        if (Dist(_pos, dest) <= size.Width * WalkFigures) StartWalk(dest, s);
        else StartVanish(dest);
    }

    private void StartWalk(PointF to, float s)
    {
        _from = _pos;
        _to = to;
        _move = Motion.Walk;
        _moveT = 0;
        _moveDur = Math.Max(0.4f, Dist(_from, _to) / (WalkSpeed * s));
        _faceLeft = _to.X < _from.X;
    }

    private void StartVanish(PointF to)
    {
        _to = to;
        _move = Motion.Vanish;
        _moveT = 0;
        _moveDur = VanishTime;
    }

    private void StartAppear(PointF at)
    {
        _pos = at;
        _move = Motion.Appear;
        _moveT = 0;
        _moveDur = AppearTime;
        _placed = true;
    }

    private void Advance(float dt, float s)
    {
        if (_move == Motion.None) return;
        _moveT = Math.Min(1, _moveT + dt / _moveDur);
        switch (_move)
        {
            case Motion.Walk:
                float e = SmoothStep(_moveT);
                _pos = new PointF(_from.X + (_to.X - _from.X) * e, _from.Y + (_to.Y - _from.Y) * e);
                // two steps per stride, about 2.6 strides per second
                _walkPhase += dt * 2.6f * MathF.PI * 2;
                if (_moveT >= 1) { _move = Motion.None; _walkPhase = 0; }
                break;
            case Motion.Vanish:
                if (_moveT >= 1) StartAppear(_to);
                break;
            case Motion.Appear:
                if (_moveT >= 1) _move = Motion.None;
                break;
        }
    }

    // =========================================================== where to stand

    private static SizeF FigureSize(float s) => new(150 * Unit * s, 140 * Unit * s);

    /// <summary>The figure's own box when centred at <paramref name="c"/>.</summary>
    private static RectangleF Box(PointF c, SizeF size) => new(c.X - size.Width / 2, c.Y - size.Height / 2, size.Width, size.Height);

    /// <summary>
    /// The pointing hand reaches the target from where it stands: shoulder (~0.45 figure widths from the centre)
    /// plus arm and finger (~0.5) — about one figure width to the nearest point of the target.
    /// </summary>
    private bool Reaches(MascotInput inp, PointF t, SizeF size, float s)
    {
        var tb = TargetBox(inp, t, s);
        float nx = Math.Clamp(_pos.X, tb.Left, tb.Right), ny = Math.Clamp(_pos.Y, tb.Top, tb.Bottom);
        return Dist(_pos, new PointF(nx, ny)) <= size.Width * 1.0f;
    }

    private static RectangleF TargetBox(MascotInput inp, PointF t, float s) =>
        inp.TargetBounds.IsEmpty ? new RectangleF(t.X - 12 * s, t.Y - 12 * s, 24 * s, 24 * s) : inp.TargetBounds;

    /// <summary>A spot is fine if it is on screen and covers neither the target nor any mark, caption or the card.</summary>
    private static bool SpotOk(MascotInput inp, PointF c, SizeF size, float s)
    {
        var box = Box(c, size);
        var work = Native.WorkAreaAt(c);
        if (!work.Contains(Rectangle.Round(box))) return false;
        if (inp.Target is PointF t && box.IntersectsWith(RectangleF.Inflate(TargetBox(inp, t, s), 6 * s, 6 * s))) return false;
        if (Engine.CardRect is Rectangle cr && cr.Width > 0 && box.IntersectsWith(cr)) return false;
        foreach (var a in inp.Avoid)
        {
            var i = RectangleF.Intersect(a, box);
            if (!i.IsEmpty && i.Width * i.Height > box.Width * box.Height * 0.08f) return false;
        }
        return true;
    }

    /// <summary>
    /// The least disturbing spot near the target, out of 16 candidates (8 directions × 2 distances):
    /// on screen; off the target, the marks, captions and the card; off the line from the mouse to the target
    /// (the user's hand travels there); over plain background rather than busy UI (judged from the window's own
    /// pixels); and, other things equal, close to where it stands now — less to move.
    /// </summary>
    private PointF ChooseSpot(MascotInput inp, PointF t, SizeF size, float s)
    {
        var tb = TargetBox(inp, t, s);
        float r = Math.Clamp(Math.Max(tb.Width, tb.Height) / 2, 10 * s, 90 * s);
        var work = Native.WorkAreaAt(t);
        var avoid = inp.Avoid.ToList();
        avoid.Add(RectangleF.Inflate(tb, 6 * s, 6 * s));
        if (Engine.CardRect is Rectangle cr && cr.Width > 0) avoid.Add(cr);
        Native.GetCursorPos(out var cp);
        var cursor = new PointF(cp.X, cp.Y);

        var cands = new List<(PointF c, float pref)>();
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4;
            var dir = new PointF(MathF.Cos(a), MathF.Sin(a));
            for (int d = 0; d < 2; d++)
            {
                float reach = (r + size.Width * 0.62f) * (d == 0 ? 1 : 1.55f);
                // below and beside reads naturally (the hand points up and across); right above is last
                float pref = (dir.Y < -0.5f ? 6 : dir.Y < 0 ? 3 : 0) + d * 5;
                cands.Add((new PointF(t.X + dir.X * reach, t.Y + dir.Y * reach * 0.9f), pref));
            }
        }

        var busy = Busyness(inp.Root, cands.Select(c => Box(c.c, size)).ToList());
        PointF best = cands[0].c;
        float bestScore = float.MaxValue;
        for (int i = 0; i < cands.Count; i++)
        {
            var (c, pref) = cands[i];
            var box = Box(c, size);
            float score = pref;
            if (!work.Contains(Rectangle.Round(box))) score += 1000;
            foreach (var a in avoid)
            {
                var x = RectangleF.Intersect(a, box);
                if (!x.IsEmpty) score += 40 + 300 * x.Width * x.Height / (box.Width * box.Height);
            }
            if (SegmentHitsBox(cursor, t, RectangleF.Inflate(box, 12 * s, 12 * s))) score += 25;
            score += busy[i] * 30;
            if (_placed) score += Dist(_pos, c) / size.Width * 2;
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    /// <summary>0 (plain background) … 1 (dense UI) for each box, from the window's own pixels.</summary>
    private static float[] Busyness(IntPtr root, List<RectangleF> boxes)
    {
        var res = new float[boxes.Count];
        if (root == IntPtr.Zero || boxes.Count == 0) return res;
        var all = boxes.Aggregate(RectangleF.Union);
        using var shot = ScreenCapture.CaptureWindow(root, Rectangle.Round(all));
        if (shot == null) return res;
        // CaptureWindow crops to the window; find where the shot sits on screen
        var wr = Native.FrameRect(root);
        var shotRect = Rectangle.Intersect(Rectangle.Round(all), new Rectangle(wr.X, wr.Y, wr.Width, wr.Height));
        var gray = Template.GrayFull(shot);
        int w = shot.Width, h = shot.Height;
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = Rectangle.Intersect(Rectangle.Round(boxes[i]), shotRect);
            if (b.Width < 4 || b.Height < 4) { res[i] = 0.5f; continue; }
            long sum = 0, n = 0;
            for (int y = b.Top - shotRect.Top; y < b.Bottom - shotRect.Top - 1 && y < h - 1; y += 2)
                for (int x = b.Left - shotRect.Left; x < b.Right - shotRect.Left - 1 && x < w - 1; x += 2)
                {
                    int p = gray[y * w + x];
                    sum += Math.Abs(p - gray[y * w + x + 1]) + Math.Abs(p - gray[(y + 1) * w + x]);
                    n++;
                }
            res[i] = n == 0 ? 0.5f : Math.Clamp(sum / (float)n / 24f, 0, 1);
        }
        return res;
    }

    private static bool SegmentHitsBox(PointF a, PointF b, RectangleF r)
    {
        for (int i = 0; i <= 16; i++)
        {
            float t = i / 16f;
            if (r.Contains(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)) return true;
        }
        return false;
    }

    // =========================================================== drawing

    private void Render(MascotInput? inp, float dt)
    {
        if (inp == null) return;
        float s = inp.Scale;
        var size = FigureSize(s);

        // appear / vanish: a pop with a little overshoot, a shrink with a spin
        float grow = 1, spin = 0;
        if (_move == Motion.Appear)
        {
            float p = _moveT;
            grow = p < 0.7f ? SmoothStep(p / 0.7f) * 1.12f : 1.12f - 0.12f * SmoothStep((p - 0.7f) / 0.3f);
        }
        else if (_move == Motion.Vanish) { grow = 1 - SmoothStep(_moveT); spin = 160 * _moveT; }
        float k = Unit * s * Math.Max(0.01f, grow);

        var fig = new RectangleF(_pos.X - size.Width * 0.85f, _pos.Y - size.Height * 1.0f, size.Width * 1.7f, size.Height * 1.75f);
        RectangleF bubble = RectangleF.Empty;
        string? say = inp.Say;
        var font = Theme.Font(13 * s);
        if (_bubble > 0.01f && say != null)
        {
            float maxW = 250 * s, pad = 10 * s;
            var ts = Theme.Measure(say, font, maxW - 2 * pad);
            float h = Math.Min(ts.Height, font.GetHeight() * 3 + 2) + 2 * pad, w = ts.Width + 2 * pad + 2;
            bubble = BubbleSpot(inp, size, w, h, s);
        }

        var area = bubble.IsEmpty ? fig : RectangleF.Union(fig, bubble);
        var rect = Rectangle.Ceiling(RectangleF.Inflate(area, 6, 6));
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            Theme.Quality(g);
            g.Clear(Color.Transparent);
            g.TranslateTransform(-rect.X, -rect.Y);
            if (_move == Motion.Appear || _move == Motion.Vanish) DrawPoof(g, s);
            if (grow > 0.02f) DrawFigure(g, inp, k, spin);
            if (!bubble.IsEmpty) DrawBubble(g, bubble, say!, font, s);
        }
        Present(bmp, rect);
    }

    /// <summary>A ring of sparks where it vanishes / appears.</summary>
    private void DrawPoof(Graphics g, float s)
    {
        float p = _moveT, rad = (14 + 40 * p) * s;
        int a = (int)(220 * (1 - p) * _alpha);
        using var b = new SolidBrush(Color.FromArgb(a, Palette.Accent));
        using var w = new SolidBrush(Color.FromArgb(a, White));
        for (int i = 0; i < 8; i++)
        {
            float ang = i * MathF.PI / 4 + p * 0.6f, d = 4 * s * (1 - p) + 2 * s;
            var c = new PointF(_pos.X + MathF.Cos(ang) * rad, _pos.Y + MathF.Sin(ang) * rad);
            g.FillEllipse(i % 2 == 0 ? b : w, c.X - d, c.Y - d, 2 * d, 2 * d);
        }
    }

    private enum Side { Above, Below, Away, Toward }
    private Side _bubbleSide = Side.Above;

    /// <summary>
    /// The bubble never covers the target (or a mark, a caption, the card): above, below, or beside the figure on
    /// the side away from the target, whichever is clear; the current side wins ties so it does not jump.
    /// </summary>
    private RectangleF BubbleSpot(MascotInput inp, SizeF fig, float w, float h, float s)
    {
        var work = Native.WorkAreaAt(_pos);
        var avoid = inp.Avoid.ToList();
        if (inp.Target is PointF t) avoid.Add(RectangleF.Inflate(TargetBox(inp, t, s), 10 * s, 10 * s));
        if (Engine.CardRect is Rectangle cr && cr.Width > 0) avoid.Add(cr);
        float away = _flip ? 1 : -1, gap = 10 * s;
        RectangleF At(Side side) => side switch
        {
            Side.Above => new RectangleF(_pos.X - w / 2, _pos.Y - fig.Height * 0.62f - h - gap, w, h),
            Side.Below => new RectangleF(_pos.X - w / 2, _pos.Y + fig.Height * 0.62f + gap, w, h),
            Side.Away => new RectangleF(away < 0 ? _pos.X - fig.Width * 0.6f - w - gap : _pos.X + fig.Width * 0.6f + gap, _pos.Y - h / 2, w, h),
            _ => new RectangleF(away < 0 ? _pos.X + fig.Width * 0.6f + gap : _pos.X - fig.Width * 0.6f - w - gap, _pos.Y - h / 2, w, h),
        };
        RectangleF Clamp(RectangleF r) => new(Math.Clamp(r.X, work.Left + 4, Math.Max(work.Left + 4, work.Right - r.Width - 4)),
                                              Math.Clamp(r.Y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - r.Height - 4)), r.Width, r.Height);
        Side best = _bubbleSide;
        float bestScore = float.MaxValue;
        foreach (Side side in Enum.GetValues(typeof(Side)))
        {
            var raw = At(side);
            var r = Clamp(raw);
            float score = (int)side * 3 + (side == _bubbleSide ? -5 : 0) + (Math.Abs(r.X - raw.X) + Math.Abs(r.Y - raw.Y)) / s;
            foreach (var a in avoid)
            {
                var i = RectangleF.Intersect(a, r);
                if (!i.IsEmpty) score += 400 * i.Width * i.Height / (r.Width * r.Height) + 50;
            }
            if (score < bestScore) { bestScore = score; best = side; }
        }
        _bubbleSide = best;
        return Clamp(At(best));
    }

    private void DrawBubble(Graphics g, RectangleF r, string text, Font font, float s)
    {
        int a = (int)(255 * _bubble * _alpha);
        using (var shadow = new SolidBrush(Color.FromArgb(a / 3, 0, 0, 0)))
        using (var sp = Theme.Rounded(new RectangleF(r.X + 2, r.Y + 4, r.Width, r.Height), 12 * s)) g.FillPath(shadow, sp);
        using (var path = Theme.Rounded(r, 12 * s))
        using (var b = new SolidBrush(Color.FromArgb(a, White)))
        {
            g.FillPath(b, path);
            // tail towards the figure, from whichever edge faces it
            if (r.Bottom < _pos.Y || r.Top > _pos.Y)
            {
                bool above = r.Bottom < _pos.Y;
                float tx = Math.Clamp(_pos.X, r.Left + 16 * s, r.Right - 16 * s), ty = above ? r.Bottom : r.Top, dy = above ? 9 * s : -9 * s;
                g.FillPolygon(b, new[] { new PointF(tx - 8 * s, ty), new PointF(tx + 8 * s, ty), new PointF(tx, ty + dy) });
            }
            else
            {
                bool left = r.Right < _pos.X;
                float ty = Math.Clamp(_pos.Y, r.Top + 12 * s, r.Bottom - 12 * s), tx = left ? r.Right : r.Left, dx = left ? 9 * s : -9 * s;
                g.FillPolygon(b, new[] { new PointF(tx, ty - 8 * s), new PointF(tx, ty + 8 * s), new PointF(tx + dx, ty) });
            }
        }
        using var tb = new SolidBrush(Color.FromArgb(a, Pupil));
        var pad = 10 * s;
        g.DrawString(text, font, tb, new RectangleF(r.X + pad, r.Y + pad, r.Width - 2 * pad + 2, r.Height - 2 * pad),
                     new StringFormat(Theme.Wrap) { Trimming = StringTrimming.EllipsisWord });
    }

    /// <summary>The figure in design units: body 62..198, arms from the shoulders (64,150) / (196,134), legs at x 108 / 152.</summary>
    private void DrawFigure(Graphics g, MascotInput inp, float k, float spin)
    {
        var state = g.Save();
        float t = _time;
        bool walking = _move == Motion.Walk;
        bool moving = _move != Motion.None;
        bool pointing = inp.Target is PointF && !moving && !inp.Thinking;

        // facing: while walking, the way it goes; standing, the hand faces the target (mirrored when right of it)
        if (inp.Target is PointF tp0 && !walking) _flip = tp0.X < _pos.X;
        bool flip = walking ? _faceLeft : _flip;

        float bob = walking ? -MathF.Abs(MathF.Sin(_walkPhase)) * 7 : moving ? 0 : MathF.Sin(t * MathF.PI * 2 / 2.6f) * 4;
        float rot = spin, hop = 0;
        if (!moving && (_mood == Mood.Happy || inp.Outro))
        {
            float p = inp.Outro ? (t % 1.4f) / 1.4f : 1 - _moodLeft / 0.9f;
            hop = p < 0.35f ? -28 * MathF.Sin(p / 0.35f * MathF.PI / 2) : p < 0.6f ? -28 * MathF.Cos((p - 0.35f) / 0.25f * MathF.PI / 2) : -6 * MathF.Sin((p - 0.6f) / 0.4f * MathF.PI);
        }
        if (!moving && _mood == Mood.Miss) rot = 9 * MathF.Sin(t * 22) * (_moodLeft / 1.1f);
        if (!moving && inp.Thinking) rot = -6 * MathF.Sin(t * MathF.PI * 2 / 3f);
        if (walking) rot = 3 * MathF.Sin(_walkPhase);

        g.TranslateTransform(_pos.X, _pos.Y);
        g.ScaleTransform(flip ? -k : k, k);
        g.TranslateTransform(-130, -140);
        // rotate around the feet so a head shake looks like one
        g.TranslateTransform(130, 214);
        g.RotateTransform(rot);
        g.TranslateTransform(-130, -214 + bob + hop);

        int A(int v) => (int)(v * _alpha);
        Color C(Color c) => Color.FromArgb(A(c.A), c);
        using var limb = new Pen(C(Limb), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var whiteB = new SolidBrush(C(White));

        // shadow on the ground (stays down while it hops)
        using (var sh = new SolidBrush(Color.FromArgb(A(70), 0, 0, 0))) g.FillEllipse(sh, 78, 228 - hop - bob, 104, 12);

        // legs: walking swings them in turn and lifts the foot that steps
        float swing = walking ? MathF.Sin(_walkPhase) : 0;
        float lx = 108 + swing * 9, rx = 152 - swing * 9;
        float ly = 214 - (walking ? Math.Max(0, swing) * 8 : 0), ry = 214 - (walking ? Math.Max(0, -swing) * 8 : 0);
        g.DrawLine(limb, 108, 198, lx, ly);
        g.DrawLine(limb, 152, 198, rx, ry);
        using (var lb = new SolidBrush(C(Limb)))
        {
            g.FillEllipse(lb, lx - 15 + 4, ly - 2, 22, 10);
            g.FillEllipse(lb, rx - 7, ry - 2, 22, 10);
        }
        // body
        using (var path = Theme.Rounded(new RectangleF(62, 62, 136, 136), 28))
        {
            using (var bb = new SolidBrush(C(Body))) g.FillPath(bb, path);
            using var ep = new Pen(C(Edge), 3);
            g.DrawPath(ep, path);
        }
        // progress bar on the forehead
        using (var tb = new SolidBrush(C(Track))) using (var p = Theme.Rounded(new RectangleF(84, 82, 92, 9), 4.5f)) g.FillPath(tb, p);
        float prog = Math.Clamp(inp.Outro ? 1 : inp.Progress, 0.06f, 1);
        var barColor = _mood == Mood.Happy || inp.Outro ? Palette.Success : Palette.Accent;
        using (var fb = new SolidBrush(C(barColor))) using (var p = Theme.Rounded(new RectangleF(84, 82, 92 * prog, 9), 4.5f)) g.FillPath(fb, p);

        // where the eyes look: at the target, or ahead while walking
        var look = new PointF(0, 0);
        if (walking) look = new PointF(4, 0);
        else if (inp.Target is PointF tp)
        {
            float dx = (tp.X - _pos.X) * (flip ? -1 : 1), dy = tp.Y - _pos.Y, len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
            look = new PointF(dx / len * 4, dy / len * 4);
        }
        if (inp.Thinking && !moving) look = new PointF(3, -5);

        bool happy = !moving && (_mood == Mood.Happy || inp.Outro);
        if (happy)
        {
            using var ep = new Pen(C(White), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(ep, 100, 130, 105, 118, 115, 118, 120, 130);
            g.DrawBezier(ep, 140, 130, 145, 118, 155, 118, 160, 130);
        }
        else if (_mood == Mood.Miss && !moving)
        {
            g.FillEllipse(whiteB, 97, 113, 26, 26);
            g.FillEllipse(whiteB, 137, 113, 26, 26);
            using var pb = new SolidBrush(C(Pupil));
            g.FillEllipse(pb, 112, 117, 8, 8);
            g.FillEllipse(pb, 152, 117, 8, 8);
        }
        else
        {
            float sy = _blink > 0 ? 0.12f : 1;
            g.FillEllipse(whiteB, 99, 126 - 14 * sy, 22, 28 * sy);
            g.FillEllipse(whiteB, 139, 126 - 14 * sy, 22, 28 * sy);
            if (sy > 0.5f)
            {
                using var pb = new SolidBrush(C(Pupil));
                g.FillEllipse(pb, 104.5f + look.X, 120.5f + look.Y, 11, 11);
                g.FillEllipse(pb, 144.5f + look.X, 120.5f + look.Y, 11, 11);
                g.FillEllipse(whiteB, 108 + look.X, 120 + look.Y, 3.6f, 3.6f);
                g.FillEllipse(whiteB, 148 + look.X, 120 + look.Y, 3.6f, 3.6f);
            }
        }
        // cheeks
        using (var cb = new SolidBrush(Color.FromArgb(A(115), Palette.Accent)))
        {
            g.FillEllipse(cb, 88, 145.5f, 16, 9);
            g.FillEllipse(cb, 156, 145.5f, 16, 9);
        }
        // mouth
        using (var mp = new Pen(C(White), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (happy)
            {
                using var path = new GraphicsPath();
                path.AddBezier(116, 148, 122, 166, 138, 166, 144, 148);
                path.CloseFigure();
                using (var pb = new SolidBrush(C(Pupil))) g.FillPath(pb, path);
                g.DrawPath(mp, path);
            }
            else if (_mood == Mood.Miss && !moving) g.DrawEllipse(mp, 124, 150, 12, 12);
            else if (inp.Thinking && !moving) g.DrawLine(mp, 122, 156, 140, 154);
            else g.DrawBezier(mp, 120, 152, 125, 159, 135, 159, 140, 152);
        }

        // arms
        if (happy)
        {
            g.DrawBezier(limb, 64, 130, 52, 120, 44, 106, 40, 88);
            g.DrawBezier(limb, 196, 130, 208, 120, 216, 106, 220, 88);
            g.FillEllipse(whiteB, 32, 76, 16, 16);
            g.FillEllipse(whiteB, 212, 76, 16, 16);
            DrawSparkles(g, t, A);
        }
        else if (walking)
        {
            // arms swing against the legs
            float sw = swing * 10;
            g.DrawBezier(limb, 64, 150, 52, 156, 46 - sw, 166, 44 - sw, 178);
            g.FillEllipse(whiteB, 36.5f - sw, 173.5f, 15, 15);
            g.DrawBezier(limb, 196, 150, 208, 156, 214 + sw, 166, 216 + sw, 178);
            g.FillEllipse(whiteB, 208.5f + sw, 173.5f, 15, 15);
        }
        else
        {
            if (inp.Thinking && !moving)
            {
                g.DrawBezier(limb, 64, 160, 74, 176, 98, 184, 116, 168);
                g.FillEllipse(whiteB, 110.5f, 158.5f, 15, 15);
                DrawThinkDots(g, t, A);
            }
            else
            {
                g.DrawBezier(limb, 64, 150, 52, 154, 44, 164, 42, 178);
                g.FillEllipse(whiteB, 34.5f, 173.5f, 15, 15);
            }
            // right arm: towards the target, tapping; relaxed with nothing to point at
            if (pointing && inp.Target is PointF tp2)
            {
                float dx = (tp2.X - _pos.X) * (flip ? -1 : 1), dy = tp2.Y - (_pos.Y - 6 * k);
                float ang = MathF.Atan2(dy, dx) + 0.14f * MathF.Sin(t * MathF.PI * 2 / 1.1f) * (_mood == Mood.Miss ? 2 : 1);
                ang -= rot * MathF.PI / 180;
                var dir = new PointF(MathF.Cos(ang), MathF.Sin(ang));
                var sh = new PointF(196, 134);
                var end = new PointF(sh.X + dir.X * 52, sh.Y + dir.Y * 52);
                var ctrl = new PointF(sh.X + dir.X * 26 - dir.Y * 8, sh.Y + dir.Y * 26 + dir.X * 8);
                g.DrawBezier(limb, sh, ctrl, ctrl, end);
                g.FillEllipse(whiteB, end.X - 8.5f, end.Y - 8.5f, 17, 17);
                using var finger = new Pen(C(White), 5.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(finger, end.X + dir.X * 5, end.Y + dir.Y * 5, end.X + dir.X * 17, end.Y + dir.Y * 17);
            }
            else
            {
                g.DrawBezier(limb, 196, 150, 208, 154, 216, 164, 218, 178);
                g.FillEllipse(whiteB, 210.5f, 173.5f, 15, 15);
            }
        }
        g.Restore(state);
    }

    private static void DrawSparkles(Graphics g, float t, Func<int, int> a)
    {
        var spots = new[] { (new PointF(40, 50), Palette.Warn, 0f), (new PointF(222, 44), Palette.Success, 0.33f), (new PointF(236, 150), Palette.Accent, 0.66f) };
        foreach (var (c, col, off) in spots)
        {
            float p = (t / 0.9f + off) % 1, sz = 6 + 10 * p;
            using var b = new SolidBrush(Color.FromArgb(a((int)(255 * (1 - p))), col));
            g.FillPolygon(b, new[]
            {
                new PointF(c.X, c.Y - sz), new PointF(c.X + sz * 0.3f, c.Y - sz * 0.3f), new PointF(c.X + sz, c.Y),
                new PointF(c.X + sz * 0.3f, c.Y + sz * 0.3f), new PointF(c.X, c.Y + sz), new PointF(c.X - sz * 0.3f, c.Y + sz * 0.3f),
                new PointF(c.X - sz, c.Y), new PointF(c.X - sz * 0.3f, c.Y - sz * 0.3f),
            });
        }
    }

    private static void DrawThinkDots(Graphics g, float t, Func<int, int> a)
    {
        var purple = Palette.Named["purple"];
        var dots = new[] { (206f, 58f, 7f), (226f, 40f, 9f), (244f, 20f, 11f) };
        for (int i = 0; i < dots.Length; i++)
        {
            float p = 0.15f + 0.85f * (0.5f + 0.5f * MathF.Sin((t - i * 0.2f) * MathF.PI * 2 / 1.2f));
            using var b = new SolidBrush(Color.FromArgb(a((int)(255 * p)), purple));
            var (x, y, r) = dots[i];
            g.FillEllipse(b, x - r, y - r, 2 * r, 2 * r);
        }
    }

    // =========================================================== layered window

    private void Present(Bitmap bmp, Rectangle rect)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        var mem = Native.CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = Native.SelectObject(mem, hbmp);
        try
        {
            var dst = new Native.POINT(rect.X, rect.Y);
            var size = new Native.SIZE(rect.Width, rect.Height);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(hbmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        if (!_visible) { Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE); _visible = true; _raisedAt = 0; }
        // Re-asserting "topmost" every frame made it swap places with the marks' canvas 30 times a second, which
        // flickered wherever the two overlap: raise it when it appears, then once a second (other apps' topmost
        // windows) is enough.
        if (_raisedAt == 0 || _time - _raisedAt > 1)
        {
            _raisedAt = Math.Max(_time, 0.001f);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private new void Hide()
    {
        if (!_visible) return;
        Native.ShowWindow(Handle, Native.SW_HIDE);
        _visible = false;
    }
}
