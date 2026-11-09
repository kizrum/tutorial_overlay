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
    public bool Away;                      // the agent is not listening: it dozes
    public string? Keys;                   // the step waits for a shortcut: it holds up a plate with it
    public Chord? KeyChord;                // the same, as keys: one key cap each on the plate
    public string? Gesture;                // "drag" | "scroll": nothing to point at, the step is a motion — it shows it
    public string ScrollDir = "down";      // the way the scroll gesture runs: up, down, left, right
    public string StepKey = "";            // a new step re-shows the bubble and may move it
    public string TargetKey = "";          // which of the step's targets: the next one of a checklist is a new spot, not a ride-along
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
        Track = Color.FromArgb(255, 42, 46, 58), Rim = Color.FromArgb(255, 138, 144, 163);
    private const float ArmLength = 56, FingerTip = 19;   // the pointing arm, design units from the shoulder

    private const float Unit = 0.44f;            // design unit → px at 100 %: the body (136 u) is ~60 px
    private const float WalkSpeed = 240;         // px/s at 100 %: slow enough to see the steps, a short hop still takes 0.4 s
    private const float WalkFigures = 3.2f;      // walk only up to this many figure widths; farther: vanish + appear
    private const float VanishTime = 0.2f, AppearTime = 1.0f;
    private const float HitTime = 0.9f, MissTime = 1.5f;
    private const float WalkStart = 0.12f, WalkStop = 0.8f;   // of a walk: lean in, steps, stop with a sway
    private const float IdleAfter = 20;          // s without anything happening: it looks round, stretches
    private const float Wide = 156;              // design units from the centre its widest props reach (the loupe)

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
    private string _spotTargetKey = "";          // which target of the step the spot was chosen for
    private DateTime _targetMovedAt;
    private bool _respot;               // the glued spot went bad while the target moved: choose again once it rests
    // No clean spot existed when this one was chosen: keep it until the target or the card moves. Without this the
    // figure re-chose every 0.7 s, hopping (vanish, appear) between equally bad spots by a crowded row of cells.
    private bool _acceptBad;
    private Rectangle _cardAtChoice;

    private Mood _mood;
    private float _moodLeft;
    private float _nextBlink = 3, _blink;
    private string _stepKey = "";
    private float _bubble;                       // 0..1 fade
    private bool _visible;
    // The card is never under its hands or feet: when the card moves onto it (dragged, grown), it fades out at once
    // and comes back at a free spot once the card has rested
    private bool _tucked;
    private Rectangle _lastCard, _triedCard;
    private DateTime _cardChangedAt;
    private float _raisedAt;
    private MascotInput? _lastInput;
    private bool _showKeys;                      // it holds up the plate with the step's keys this frame
    private int _hitN;                           // hits so far: the reactions take turns
    private DateTime _idleSince = DateTime.UtcNow;
    private Point _lastCursor;

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
            _idleSince = now;
            if (kind == "hit") { _mood = Mood.Happy; _moodLeft = HitTime; _hitN++; }
            else if (kind == "miss" && _mood != Mood.Happy) { _mood = Mood.Miss; _moodLeft = MissTime; }
            else if (kind is "tutorial_started" or "tutorial_stopped") { _placed = false; _spotKey = ""; }
        }
        if (_mood != Mood.Normal && (_moodLeft -= dt) <= 0) _mood = Mood.Normal;

        var card = Engine.CardRect is Rectangle crd && crd.Width > 0 ? crd : Rectangle.Empty;
        if (card != _lastCard) { _lastCard = card; _cardChangedAt = now; }
        if (inp != null && _placed && !_tucked && (TouchesCard(_pos, inp.Target, inp.Scale) || (_move is Motion.Walk or Motion.Vanish && TouchesCard(_to, inp.Target, inp.Scale))))
            Tuck();
        _alpha = Approach(_alpha, inp != null && !_tucked ? 1 : 0, dt * (_tucked ? 14 : 6));
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

        if (inp.StepKey != _stepKey) { _stepKey = inp.StepKey; _bubble = 0; _idleSince = now; }
        if (Native.GetCursorPos(out var cur) && (Math.Abs(cur.X - _lastCursor.X) > 3 || Math.Abs(cur.Y - _lastCursor.Y) > 3))
        {
            _lastCursor = new Point(cur.X, cur.Y);
            _idleSince = now;
        }
        if (_move != Motion.None) _idleSince = now;
        bool settled = _move == Motion.None && _alpha > 0.98f;
        bool wantBubble = inp.Say is { Length: > 0 } && settled && !inp.Thinking && _mood != Mood.Miss && !inp.Outro
                          && !SaysKeys(inp.Say, inp.Keys);
        _bubble = Approach(_bubble, wantBubble ? 1 : 0, dt * 5);

        // tell the engine the highlight can come in: standing still, fully shown, at this step's spot
        Engine.MascotSettledKey = settled && _spotKey == inp.StepKey ? inp.StepKey : null;
        Render(inp, dt);
    }

    /// <summary>
    /// The line only repeats the shortcut on the plate it holds ("Press Ctrl+Shift+P"): the plate says it, no bubble.
    /// A line that adds something ("Save the file" with Ctrl+S) keeps its bubble.
    /// </summary>
    internal static bool SaysKeys(string? say, string? keys)
    {
        if (string.IsNullOrEmpty(say) || string.IsNullOrEmpty(keys)) return false;
        static string Norm(string x) => new(x.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        string sn = Norm(say), kn = Norm(keys);
        return kn.Length > 0 && sn.Contains(kn) && sn.Length - kn.Length <= 12;
    }

    private static float Approach(float v, float target, float step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);
    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static float SmoothStep(float t) => t * t * (3 - 2 * t);
    private static float EaseInOut(float x) => x < 0.5f ? 2 * x * x : 1 - (-2 * x + 2) * (-2 * x + 2) / 2;

    // =========================================================== moving

    private void Tuck()
    {
        _tucked = true;
        _move = Motion.None;
        _walkPhase = 0;
        _triedCard = Rectangle.Empty;
    }

    /// <summary>
    /// Out of the card's way: once the card has rested and the figure has faded, appear at a spot where nothing of
    /// it touches the card. No such spot: stay away until the card changes.
    /// </summary>
    private void Untuck(MascotInput inp)
    {
        if (_alpha > 0.05f || (DateTime.UtcNow - _cardChangedAt).TotalSeconds < 0.35 || (_lastCard == _triedCard && !_lastCard.IsEmpty)) return;
        _triedCard = _lastCard;
        float s = inp.Scale;
        var size = FigureSize(s);
        PointF dest;
        if (inp.Target is PointF t) dest = ChooseSpot(inp, t, size, s);
        else
        {
            var c = _lastCard;
            dest = new PointF(c.Left - size.Width * 1.4f, c.Bottom - size.Height * 0.8f);
            if (TouchesCard(dest, null, s) || !Native.WorkAreaAt(dest).Contains(Point.Round(dest)))
                dest = new PointF(c.Right + size.Width * 1.4f, c.Bottom - size.Height * 0.8f);
        }
        if (TouchesCard(dest, inp.Target, s)) return;
        _tucked = false;
        StartAppear(dest);
        if (inp.Target is PointF tt) { _offset = new PointF(dest.X - tt.X, dest.Y - tt.Y); _spotTarget = tt; }
        _spotKey = inp.StepKey;
        _spotTargetKey = inp.TargetKey;
    }

    /// <summary>Decide where to be for this frame's target, and how to get there.</summary>
    private void Steer(MascotInput inp)
    {
        float s = inp.Scale;
        var size = FigureSize(s);
        if (_tucked) { Untuck(inp); return; }
        if (inp.Target is not PointF t)
        {
            // nothing to point at: stay; the very first time, stand by the card
            if (!_placed)
            {
                var c = Engine.CardRect is Rectangle cr && cr.Width > 0 ? cr : Screen.PrimaryScreen!.WorkingArea;
                var at = new PointF(c.Left - size.Width * 1.4f, c.Bottom - size.Height * 0.8f);
                StartAppear(at);
                if (TouchesCard(at, null, s)) { Tuck(); _alpha = 0; }
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
            if (TouchesCard(spot, t, s)) { Tuck(); _alpha = 0; }
            _offset = new PointF(spot.X - t.X, spot.Y - t.Y);
            _spotKey = inp.StepKey;
            _spotTargetKey = inp.TargetKey;
            _spotTarget = t;
            return;
        }

        // the next target of the same step (a checklist's next cell) is somewhere else to go to: walk there or turn
        // the arm, never ride along with the jump as if the window had moved
        if (_spotKey == inp.StepKey && _spotTargetKey != inp.TargetKey) _spotKey = "";
        if (_spotKey == inp.StepKey)
        {
            // same step: ride along with the target (window dragged, content scrolled)
            // no walking while it moves: ride along where that is fine, otherwise stand still and only choose a
            // new spot once the content has rested a moment
            var glued = new PointF(t.X + _offset.X, t.Y + _offset.Y);
            var cardNow = Engine.CardRect is Rectangle crNow ? crNow : Rectangle.Empty;
            if (_acceptBad && cardNow != _cardAtChoice) _acceptBad = false;   // the card moved: maybe there is room now
            if (Dist(t, _spotTarget) > 0.5f)
            {
                _spotTarget = t;
                _targetMovedAt = DateTime.UtcNow;
                _acceptBad = false;
                if (SpotOk(inp, glued, size, s)) { _pos = glued; _respot = false; }
                else _respot = true;
            }
            else if (!_respot && !_acceptBad && !SpotOk(inp, _pos, size, s))
            {
                // the target did not move but grew over us (or something else now covers the spot): step aside
                _respot = true;
                _targetMovedAt = DateTime.UtcNow;
            }
            if (!_respot || (DateTime.UtcNow - _targetMovedAt).TotalSeconds < 0.7) return;
            _respot = false;
            _spotKey = "";
        }

        // a new step (or a spot that went bad)
        _respot = false;
        _spotKey = inp.StepKey;
        _spotTargetKey = inp.TargetKey;
        _spotTarget = t;
        if (Reaches(inp, t, size, s) && SpotOk(inp, _pos, size, s))
        {
            // the hand reaches from here: just point the other way
            _offset = new PointF(_pos.X - t.X, _pos.Y - t.Y);
            return;
        }
        var dest = ChooseSpot(inp, t, size, s);
        if (TouchesCard(dest, t, s)) { Tuck(); return; }   // nowhere free of the card: rather not be seen
        _acceptBad = !SpotOk(inp, dest, size, s);
        _cardAtChoice = Engine.CardRect is Rectangle crc ? crc : Rectangle.Empty;
        _offset = new PointF(dest.X - t.X, dest.Y - t.Y);
        if (Dist(_pos, dest) < size.Width * 0.3f) return;     // the best there is is about here: no shuffling on the spot
        if (Dist(_pos, dest) <= size.Width * WalkFigures) StartWalk(dest, s);
        else StartVanish(dest);
    }

    private void StartWalk(PointF to, float s)
    {
        _from = _pos;
        _to = to;
        _move = Motion.Walk;
        _moveT = 0;
        // the steps take the middle of the walk; before them it leans in, after them it stops with a sway
        _moveDur = Math.Max(0.4f, Dist(_from, _to) / (WalkSpeed * s)) / (WalkStop - WalkStart);
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
                float q = Math.Clamp((_moveT - WalkStart) / (WalkStop - WalkStart), 0, 1);
                float e = EaseInOut(q);
                _pos = new PointF(_from.X + (_to.X - _from.X) * e, _from.Y + (_to.Y - _from.Y) * e);
                // two steps per stride, about 2.6 strides per second
                if (q > 0 && q < 1) _walkPhase += dt * 2.6f * MathF.PI * 2;
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

    /// <summary>
    /// Everything the figure may draw when centred at <paramref name="c"/>: hands out and up, feet and shadow, the
    /// sparkles and thinking dots over its head, and the pointing arm to the fingertip (it faces the target).
    /// </summary>
    internal static RectangleF[] Reach(PointF c, PointF? target, float s)
    {
        float u = Unit * s;
        bool flip = target is PointF t0 && t0.X < c.X;
        float m = flip ? -1 : 1;
        // design units around the centre (130,140): hands 30..230, sparkles to 256, dots up to y 9, shadow to 240
        var all = RectangleF.FromLTRB(c.X - Wide * u, c.Y - 148 * u, c.X + Wide * u, c.Y + 102 * u);
        if (target is not PointF t) return new[] { all };
        var sh = new PointF(c.X + m * 66 * u, c.Y - 6 * u);
        float dx = t.X - sh.X, dy = t.Y - sh.Y, len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
        float reach = (ArmLength + FingerTip + 12) * u;
        var tip = new PointF(sh.X + dx / len * reach, sh.Y + dy / len * reach);
        var arm = RectangleF.FromLTRB(Math.Min(sh.X, tip.X), Math.Min(sh.Y, tip.Y), Math.Max(sh.X, tip.X), Math.Max(sh.Y, tip.Y));
        return new[] { all, RectangleF.Inflate(arm, 14 * u, 14 * u) };
    }

    /// <summary>Would any part of the figure at <paramref name="c"/> touch the card (with a small gap)?</summary>
    private static bool TouchesCard(PointF c, PointF? target, float s) =>
        Engine.CardRect is Rectangle cr && cr.Width > 0 && Touches(c, target, s, cr);

    /// <summary>Would any part of the figure at <paramref name="c"/> touch <paramref name="card"/> (with a small gap)?</summary>
    internal static bool Touches(PointF c, PointF? target, float s, Rectangle card)
    {
        var box = RectangleF.Inflate(card, 4 * s, 4 * s);
        foreach (var r in Reach(c, target, s)) if (r.IntersectsWith(box)) return true;
        return false;
    }

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
        if (TouchesCard(c, inp.Target, s)) return false;
        foreach (var a in inp.Avoid)
        {
            var i = RectangleF.Intersect(a, box);
            if (!i.IsEmpty && i.Width * i.Height > box.Width * box.Height * 0.08f) return false;
        }
        return true;
    }

    /// <summary>
    /// The least disturbing spot near the target, out of 24 candidates (8 directions × 3 distances):
    /// on screen; off the target, the marks, captions and the card; off the line from the mouse to the target
    /// (the user's hand travels there); over plain background rather than busy UI (judged from the window's own
    /// pixels); and, other things equal, close to where it stands now — less to move.
    /// </summary>
    private PointF ChooseSpot(MascotInput inp, PointF t, SizeF size, float s)
    {
        var tb = TargetBox(inp, t, s);
        var work = Native.WorkAreaAt(t);
        var avoid = inp.Avoid.ToList();
        avoid.Add(RectangleF.Inflate(tb, 6 * s, 6 * s));
        RectangleF card = Engine.CardRect is Rectangle cr && cr.Width > 0 ? cr : RectangleF.Empty;
        Native.GetCursorPos(out var cp);
        var cursor = new PointF(cp.X, cp.Y);

        var cands = new List<(PointF c, float pref)>();
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4;
            var dir = new PointF(MathF.Cos(a), MathF.Sin(a));
            for (int d = 0; d < 3; d++)
            {
                // out from the target's own edge (a dialog-sized target is far wider than the figure), then the
                // figure's half-size that way plus a gap; the second and third rings farther out, for when a row of
                // marks and the card leave no room close by
                float edge = EdgeDistance(tb, t, dir) + 6 * s;
                float half = MathF.Abs(dir.X) * size.Width / 2 + MathF.Abs(dir.Y) * size.Height / 2;
                float reach = edge + (half + 8 * s) * (d == 0 ? 1 : d == 1 ? 1.55f : 2.3f);
                // below and beside reads naturally (the hand points up and across); right above is last
                float pref = (dir.Y < -0.5f ? 6 : dir.Y < 0 ? 3 : 0) + d * 5;
                cands.Add((new PointF(t.X + dir.X * reach, t.Y + dir.Y * reach), pref));
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
            // the card is what the user reads and presses: no hand, foot or sparkle on it, ever
            if (!card.IsEmpty && TouchesCard(c, t, s)) score += 5000;
            if (SegmentHitsBox(cursor, t, RectangleF.Inflate(box, 12 * s, 12 * s))) score += 25;
            score += busy[i] * 30;
            if (_placed) score += Dist(_pos, c) / size.Width * 2;
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    /// <summary>How far from t (inside or at the box) the ray along dir leaves the box.</summary>
    private static float EdgeDistance(RectangleF box, PointF t, PointF dir)
    {
        float tx = dir.X > 1e-4f ? (box.Right - t.X) / dir.X : dir.X < -1e-4f ? (box.Left - t.X) / dir.X : float.MaxValue;
        float ty = dir.Y > 1e-4f ? (box.Bottom - t.Y) / dir.Y : dir.Y < -1e-4f ? (box.Top - t.Y) / dir.Y : float.MaxValue;
        return Math.Max(0, Math.Min(tx, ty));
    }

    /// <summary>0 (plain background) … 1 (dense UI) for each box, from the window's own pixels.</summary>
    private static float[] Busyness(IntPtr root, List<RectangleF> boxes)
    {
        var res = new float[boxes.Count];
        if (root == IntPtr.Zero || boxes.Count == 0) return res;
        var all = boxes.Aggregate(RectangleF.Union);
        // how busy the background is changes slowly: a print up to 2 s old (the finder's, say) will do, so a new
        // spot rarely costs a whole-window print on the UI thread
        using var shot = ScreenCapture.CaptureWindow(root, Rectangle.Round(all), maxAgeMs: 2000);
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
        float u = Unit * s;

        // vanish: a shrink with a spin; appear: a fall from above, from lower down when the card is right above
        float grow = 1, spin = 0;
        if (_move == Motion.Vanish) { grow = 1 - SmoothStep(_moveT); spin = 160 * _moveT; }
        float dropMax = 140;
        if (_move == Motion.Appear && Engine.CardRect is Rectangle cr0 && cr0.Width > 0
            && RectangleF.FromLTRB(_pos.X - 70 * u, _pos.Y - 220 * u, _pos.X + 70 * u, _pos.Y - 78 * u).IntersectsWith(cr0))
            dropMax = 50;
        var pose = PoseFor(inp, dropMax);
        _showKeys = pose.ArmsHold;
        float k = u * Math.Max(0.01f, grow);

        var fig = RectangleF.FromLTRB(_pos.X - (Wide + 6) * u, _pos.Y - (152 + (_move == Motion.Appear ? dropMax : 0)) * u,
                                      _pos.X + (Wide + 6) * u, _pos.Y + 108 * u);
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
            if (_move == Motion.Vanish) DrawPoof(g, s);
            if (grow > 0.02f)
            {
                // nothing it draws (confetti, a loupe, the drag's dot) leaves the box that is kept off the card
                var st = g.Save();
                if (_move != Motion.Appear)
                {
                    using var clip = new Region(RectangleF.Empty);
                    foreach (var r in Reach(_pos, inp.Target, s)) clip.Union(r);
                    g.Clip = clip;
                }
                DrawFigure(g, inp, k, spin, pose);
                g.Restore(st);
            }
            if (!bubble.IsEmpty) DrawBubble(g, bubble, say!, font, s);
        }
        Present(bmp, rect);
    }

    /// <summary>A ring of sparks where it vanishes.</summary>
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
            Side.Above => new RectangleF(_pos.X - w / 2, _pos.Y - fig.Height * 0.62f - (_showKeys ? 46 * Unit * s : 0) - h - gap, w, h),
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

    internal enum Eyes { Open, Wide, Happy }
    internal enum Mouth { Smile, Grin, O, Flat, Sleep }

    /// <summary>
    /// One frame of the figure, in design units — the same pose model as the design sheet: where the body is and how it
    /// is squashed, what the face and each arm do, which props and effects are out.
    /// </summary>
    internal sealed class Pose
    {
        public float Bob, Hop, Rot, Sx = 1, Sy = 1, FaceX, FaceY, Lid, TapK = 1, Reach, Breath = 2.6f, Prog, KeyPhase;
        public Eyes Eyes;
        public Mouth Mouth;
        public PointF? Look, ArmR;
        public bool Point = true, Happy, Wink, Brow, Sparkle, Dots, Zz, BarOk, ArmsUp, ArmsHold, KeyDown, Lean, Loupe;
        public string ScrollDir = "down";
        public float? Glint, Confetti, DanceArm, Stretch, Thumb, Drag, Scroll, Walk, Dust;
    }

    /// <summary>
    /// What it does now, one thing at a time: moving (appear, walk) first, then the end, a hit, a miss, the agent
    /// looking into a question, the step's shortcut or gesture, the agent away, a long quiet spell, and pointing.
    /// </summary>
    private Pose PoseFor(MascotInput inp, float dropMax)
    {
        var P = new Pose { Prog = inp.Progress, Point = inp.Target != null };
        float t = _time;
        if (_move == Motion.Appear) Appear(P, _moveT, dropMax);
        else if (_move == Motion.Walk) WalkPose(P, _moveT, _walkPhase);
        else if (_move == Motion.Vanish) P.Point = false;
        else if (inp.Outro)
        {
            float b = t * MathF.PI * 2 / 1.1f;
            P.Happy = true; P.Point = false; P.BarOk = true; P.Prog = 1;
            P.Rot = 9 * MathF.Sin(b); P.Hop = -14 * MathF.Abs(MathF.Sin(b)); P.Sy = 1 - 0.05f * MathF.Abs(MathF.Cos(b));
            P.DanceArm = MathF.Sin(b); P.Confetti = t;
        }
        else if (_mood == Mood.Happy) Hit(P, 1 - _moodLeft / HitTime, _hitN);
        else if (_mood == Mood.Miss) Miss(P, 1 - _moodLeft / MissTime);
        else if (inp.Thinking)
        {
            // the agent is looking into the user's question: it looks at the target through a loupe
            P.Point = false; P.Loupe = true; P.Dots = true; P.Rot = 2 * MathF.Sin(t * 1.4f); P.Mouth = Mouth.Flat;
        }
        else if (inp.KeyChord != null)
        {
            P.Point = false; P.ArmsHold = true; P.Look = new PointF(0, -4);
            P.KeyPhase = t % KeyCycle;
            P.KeyDown = P.KeyPhase >= CapDownAt(CapLabels(inp.KeyChord.Value).Length) && P.KeyPhase < AllUpAt(CapLabels(inp.KeyChord.Value).Length);
        }
        else if (inp.Gesture == "drag") { P.Point = false; P.Drag = t % 1.8f / 1.8f; }
        else if (inp.Gesture == "scroll") { P.Point = false; P.Scroll = t; P.ScrollDir = inp.ScrollDir; }
        else if (inp.Away)
        {
            P.Point = false; P.Lid = 0.82f + 0.08f * MathF.Sin(t * 1.2f); P.Sy = 0.97f + 0.015f * MathF.Sin(t * 1.6f);
            P.Mouth = Mouth.Sleep; P.Zz = true; P.Rot = -4;
        }
        else if (inp.Target != null && (DateTime.UtcNow - _idleSince).TotalSeconds > IdleAfter)
            Idle(P, (float)((DateTime.UtcNow - _idleSince).TotalSeconds - IdleAfter));
        return P;
    }

    /// <summary>Drops in: falls stretched, wide-eyed; squashes on landing with dust; bounces back to its shape.</summary>
    internal static void Appear(Pose P, float p, float dropMax)
    {
        if (p < 0.35f)
        {
            float q = p / 0.35f;
            P.Hop = -dropMax * (1 - q * q); P.Sx = 0.9f; P.Sy = 1.14f; P.Eyes = Eyes.Wide; P.Point = false;
            P.ArmR = new PointF(212, 100);
        }
        else if (p < 0.5f)
        {
            float q = (p - 0.35f) / 0.15f, w = MathF.Sin(q * MathF.PI);
            P.Sx = 1 + 0.2f * w; P.Sy = 1 - 0.2f * w; P.Dust = q;
        }
        else if (p < 0.75f)
        {
            float q = (p - 0.5f) / 0.25f, w = MathF.Sin(q * MathF.PI);
            P.Sx = 1 - 0.05f * w; P.Sy = 1 + 0.07f * w; P.Hop = -6 * w; P.Dust = 1 + q;
        }
    }

    /// <summary>A walk: it leans in before the first step, steps with a push and a squash, stops with a sway.</summary>
    private static void WalkPose(Pose P, float p, float phase)
    {
        P.Point = false;
        if (p < WalkStart)
        {
            float q = p / WalkStart;
            P.Rot = -6 * MathF.Sin(q * MathF.PI / 2); P.Sy = 1 - 0.04f * q;
        }
        else if (p < WalkStop)
        {
            float c = MathF.Abs(MathF.Cos(phase));
            P.Walk = phase; P.Bob = -MathF.Abs(MathF.Sin(phase)) * 8;
            P.Sy = 0.95f + 0.05f * (1 - c); P.Sx = 1.04f - 0.04f * (1 - c); P.Rot = 6; P.Lean = true;
        }
        else
        {
            float q = (p - WalkStop) / (1 - WalkStop), w = MathF.Sin(q * MathF.PI);
            P.Rot = -5 * w * (1 - q); P.Sx = 1 + 0.04f * w; P.Sy = 1 - 0.04f * w;
        }
    }

    /// <summary>A hit: three reactions in turn — a wind-up and a jump, a thumbs-up with a wink, a nod.</summary>
    internal static void Hit(Pose P, float p, int n)
    {
        P.Glint = p; P.BarOk = true;
        switch (n % 3)
        {
            case 1:
                P.Happy = true; P.Point = false; P.ArmsUp = p > 0.15f;
                if (p < 0.15f) { float q = p / 0.15f; P.Sy = 1 - 0.12f * q; P.Sx = 1 + 0.1f * q; }
                else if (p < 0.5f) { float q = (p - 0.15f) / 0.35f; P.Hop = -34 * MathF.Sin(q * MathF.PI); P.Sy = 1.08f; P.Sx = 0.94f; }
                else if (p < 0.65f) { float q = (p - 0.5f) / 0.15f, w = MathF.Sin(q * MathF.PI); P.Sy = 1 - 0.14f * w; P.Sx = 1 + 0.14f * w; }
                P.Sparkle = p > 0.2f && p < 0.85f;
                break;
            case 2:
                P.Point = false;
                P.Thumb = Math.Min(1, p / 0.25f) * (p > 0.85f ? (1 - p) / 0.15f : 1);
                P.Wink = p > 0.15f && p < 0.8f; P.Mouth = Mouth.Grin; P.Bob = -3 * MathF.Sin(p * MathF.PI * 2);
                break;
            default:
                P.Mouth = Mouth.Grin; P.FaceY = 5 * Math.Max(0, MathF.Sin(p * MathF.PI * 4));
                P.Eyes = p < 0.8f ? Eyes.Happy : Eyes.Open;
                break;
        }
    }

    /// <summary>A miss: the face shakes "no" (the body stays), then it points again, insisting — the target flashes.</summary>
    internal static void Miss(Pose P, float p)
    {
        if (p < 0.55f)
        {
            float q = p / 0.55f;
            P.FaceX = 8 * MathF.Sin(q * MathF.PI * 5) * (1 - q * 0.6f); P.Mouth = Mouth.Flat; P.Lid = 0.3f;
            P.Point = false; P.ArmR = new PointF(214, 176);
        }
        else
        {
            float q = (p - 0.55f) / 0.45f;
            P.TapK = 3.2f * (1 - q * 0.5f); P.Reach = 6 * MathF.Sin(q * MathF.PI * 3) * (1 - q); P.Brow = true;
        }
    }

    /// <summary>Nothing for a while: it looks one way, the other, stretches, and points again (a 7 s round).</summary>
    private static void Idle(Pose P, float t)
    {
        float c = t % 7;
        if (c < 1.4f) { P.Look = new PointF(-5, 0); P.Point = false; }
        else if (c < 2.8f) { P.Look = new PointF(5, 1); P.Point = false; }
        else if (c < 4.2f)
        {
            float st = MathF.Sin((c - 2.8f) / 1.4f * MathF.PI);
            P.Point = false; P.Stretch = st; P.Sy = 1 + 0.06f * st; P.Sx = 1 - 0.04f * st;
            P.Eyes = st > 0.5f ? Eyes.Happy : Eyes.Open;
        }
    }

    /// <summary>
    /// The figure in design units ("Шажок+"): body 62..198 with a light rim and a soft top light, arms from the
    /// shoulders with mitten hands, capsule feet at x 108 / 152. Limbs carry a thin dark outline so they read on light
    /// apps as well as dark ones. Everything it draws stays inside <see cref="Reach"/> (the caller clips to it).
    /// </summary>
    private void DrawFigure(Graphics g, MascotInput inp, float k, float spin, Pose P)
    {
        var state = g.Save();
        float t = _time;
        bool walking = _move == Motion.Walk;

        // facing: while walking, the way it goes; standing, the hand faces the target (mirrored when right of it)
        if (inp.Target is PointF tp0 && !walking) _flip = tp0.X < _pos.X;
        bool flip = walking ? _faceLeft : _flip;
        float fx = flip ? -1 : 1;

        float bob = P.Bob + MathF.Sin(t * MathF.PI * 2 / P.Breath) * (P.Point ? 3 : 2);
        float rot = P.Rot + spin;

        // shadow and dust on the ground, outside the body's squash
        g.TranslateTransform(_pos.X, _pos.Y);
        g.ScaleTransform(fx * k, k);
        g.TranslateTransform(-130, -140);
        int A(int v) => (int)(v * _alpha);
        Color C(Color c) => Color.FromArgb(A(c.A), c);
        using (var sh = new SolidBrush(Color.FromArgb(A(70), 0, 0, 0)))
        {
            float sw0 = 52 * (1 - Math.Min(0.4f, -P.Hop / 200));
            g.FillEllipse(sh, 130 - sw0, 228, 2 * sw0, 12);
        }
        if (P.Dust is float dust && dust < 2)
        {
            float q = dust / 2;
            using var db = new SolidBrush(Color.FromArgb(A(130), Rim));
            for (int i = 0; i < 5; i++)
            {
                int side = i < 3 ? -1 : 1;
                float r = 5 * (1 - q) + 1, x = 130 + side * (40 + q * 50 + i * 6), y = 230 - q * 14 - (i % 3) * 4;
                g.FillEllipse(db, x - r, y - r, 2 * r, 2 * r);
            }
        }
        // squash and turn around the feet
        g.TranslateTransform(130, 214);
        g.RotateTransform(rot);
        g.ScaleTransform(P.Sx, P.Sy);
        g.TranslateTransform(-130, -214 + bob + P.Hop);

        using var limb = new Pen(C(Limb), 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var under = new Pen(Color.FromArgb(A(150), Pupil), 11.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var whiteB = new SolidBrush(C(White));
        using var rimP = new Pen(Color.FromArgb(A(150), Pupil), 2.5f);

        void Line(float x1, float y1, float x2, float y2) { g.DrawLine(under, x1, y1, x2, y2); g.DrawLine(limb, x1, y1, x2, y2); }
        void Hand(float x, float y, float r = 9.5f) { g.FillEllipse(whiteB, x - r, y - r, 2 * r, 2 * r); g.DrawEllipse(rimP, x - r, y - r, 2 * r, 2 * r); }
        // an arm from the shoulder S to the hand H, bowed sideways by "bend"; returns its direction at the hand
        PointF Arm(PointF S, PointF H, float bend, bool hand = true)
        {
            float dx = H.X - S.X, dy = H.Y - S.Y, L = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy)), ux = dx / L, uy = dy / L;
            var c = new PointF(S.X + dx * 0.5f - uy * bend, S.Y + dy * 0.5f + ux * bend);
            var c1 = new PointF(S.X + (c.X - S.X) * 2 / 3, S.Y + (c.Y - S.Y) * 2 / 3);
            var c2 = new PointF(H.X + (c.X - H.X) * 2 / 3, H.Y + (c.Y - H.Y) * 2 / 3);
            g.DrawBezier(under, S, c1, c2, H);
            g.DrawBezier(limb, S, c1, c2, H);
            if (hand) Hand(H.X, H.Y);
            return new PointF(ux, uy);
        }
        void Finger(PointF H, PointF d, float from, float to, float w = 6)
        {
            using (var fu = new Pen(Color.FromArgb(A(150), Pupil), w + 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(fu, H.X + d.X * from, H.Y + d.Y * from, H.X + d.X * to, H.Y + d.Y * to);
            using var fp = new Pen(C(White), w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(fp, H.X + d.X * from, H.Y + d.Y * from, H.X + d.X * to, H.Y + d.Y * to);
        }

        // legs: walking swings them in turn and lifts the foot that steps
        float swing = P.Walk is float wph ? MathF.Sin(wph) : 0;
        float lx = 108 + swing * 9, rx = 152 - swing * 9;
        float ly = 214 - Math.Max(0, swing) * 12, ry = 214 - Math.Max(0, -swing) * 12;
        Line(108, 198, lx, ly);
        Line(152, 198, rx, ry);
        using (var lb = new SolidBrush(C(Limb)))
        {
            foreach (var f in new[] { new RectangleF(lx - 13, ly - 2, 24, 11), new RectangleF(rx - 9, ry - 2, 24, 11) })
            {
                using var p = Theme.Rounded(f, 5.5f);
                g.FillPath(lb, p);
                g.DrawPath(rimP, p);
            }
        }
        // body: a soft light from above and a light rim, so it stands out from a dark app
        var bodyR = new RectangleF(62, 62, 136, 136);
        using (var path = Theme.Rounded(bodyR, 34))
        {
            using (var bb = new SolidBrush(C(Body))) g.FillPath(bb, path);
            // over the whole body height, fading out by its middle (a shorter brush would tile and band the body)
            using (var lg = new LinearGradientBrush(new PointF(0, 60), new PointF(0, 200), Color.White, Color.White))
            {
                lg.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(A(16), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                    Positions = new[] { 0f, 0.4f, 1f },
                };
                g.FillPath(lg, path);
            }
            using var ep = new Pen(C(Rim), 3.5f);
            g.DrawPath(ep, path);
        }
        using (var glow = Theme.Rounded(RectangleF.Inflate(bodyR, 4.5f, 4.5f), 38.5f))
        using (var gp = new Pen(Color.FromArgb(A(80), Palette.Accent), 1.5f))
            g.DrawPath(gp, glow);

        // progress bar on the forehead; a glint runs along it on a hit
        using (var tb = new SolidBrush(C(Track))) using (var p = Theme.Rounded(new RectangleF(84, 82, 92, 9), 4.5f)) g.FillPath(tb, p);
        float prog = Math.Clamp(P.Prog, 0.06f, 1);
        var barColor = P.BarOk ? Palette.Success : Palette.Accent;
        using (var fb = new SolidBrush(C(barColor))) using (var p = Theme.Rounded(new RectangleF(84, 82, 92 * prog, 9), 4.5f)) g.FillPath(fb, p);
        if (P.Glint is float gl && gl < 1)
        {
            float gx = 84 + 92 * prog * gl;
            using var gb = new SolidBrush(Color.FromArgb(A(215), 255, 255, 255));
            using var gpth = Theme.Rounded(new RectangleF(gx - 5, 82, 10, 9), 4.5f);
            g.FillPath(gb, gpth);
        }

        // where the eyes look: the pose's own look; the cursor when it comes close while it points; the target; ahead
        PointF ToTarget(float m)
        {
            if (inp.Target is not PointF tp) return PointF.Empty;
            float dx = (tp.X - _pos.X) * fx, dy = tp.Y - _pos.Y, len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
            return new PointF(dx / len * m, dy / len * m);
        }
        var look = PointF.Empty;
        if (P.Look is PointF pl) look = pl;
        else if (P.Point && Native.GetCursorPos(out var cp) && Dist(new PointF(cp.X, cp.Y), _pos) < 240 * inp.Scale
                 && Dist(new PointF(cp.X, cp.Y), _pos) > 10)
        {
            float dx = (cp.X - _pos.X) * fx, dy = cp.Y - _pos.Y, len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
            look = new PointF(dx / len * 5, dy / len * 4);
        }
        else if (P.Point) look = ToTarget(4);
        else if (P.Lean) look = new PointF(4, 0);
        if (P.Loupe) look = ToTarget(5);

        // the face (it moves on its own: a head shake, a nod)
        g.TranslateTransform(P.FaceX, P.FaceY);
        var eyes = P.Happy ? Eyes.Happy : P.Eyes;
        float lid = _blink > 0 && eyes == Eyes.Open ? 0.9f : P.Lid;
        using (var pb = new SolidBrush(C(Pupil)))
        using (var arcP = new Pen(C(White), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var lidP = new Pen(C(Rim), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var browP = new Pen(C(White), 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            foreach (var (ex, left) in new[] { (110f, true), (150f, false) })
            {
                if (eyes == Eyes.Happy || (P.Wink && left))
                {
                    g.DrawBezier(arcP, ex - 10, 130, ex - 5, 118, ex + 5, 118, ex + 10, 130);
                    continue;
                }
                bool wide = eyes == Eyes.Wide;
                float rw = wide ? 13 : 11, rh = wide ? 13 : 14, cy = 126;
                var st = g.Save();
                if (lid > 0) g.SetClip(new RectangleF(ex - 20, cy - rh + 2 * rh * lid, 40, 40), CombineMode.Intersect);
                g.FillEllipse(whiteB, ex - rw, cy - rh, 2 * rw, 2 * rh);
                float pr = wide ? 4 : 5.5f;
                g.FillEllipse(pb, ex + look.X - pr, cy - 0.5f + look.Y - pr, 2 * pr, 2 * pr);
                g.FillEllipse(whiteB, ex + look.X + 1.6f - 1.8f, cy - 3 + look.Y - 1.8f, 3.6f, 3.6f);
                g.Restore(st);
                if (lid > 0.05f) { float ly2 = cy - rh + 2 * rh * lid; g.DrawLine(lidP, ex - rw + 1, ly2, ex + rw - 1, ly2); }
                if (P.Brow) g.DrawLine(browP, ex - 9, 106 + (left ? 4 : 0), ex + 9, 106 + (left ? 0 : 4));
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
            switch (P.Happy ? Mouth.Grin : P.Mouth)
            {
                case Mouth.Grin:
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(116, 148, 122, 166, 138, 166, 144, 148);
                        path.CloseFigure();
                        using (var mb = new SolidBrush(C(Pupil))) g.FillPath(mb, path);
                        g.DrawPath(mp, path);
                    }
                    break;
                case Mouth.O: g.DrawEllipse(mp, 124, 150, 12, 12); break;
                case Mouth.Flat: g.DrawLine(mp, 122, 156, 140, 154); break;
                case Mouth.Sleep: { float ry2 = 3 + MathF.Sin(t * 1.2f); g.DrawEllipse(mp, 127, 157 - ry2, 8, 2 * ry2); break; }
                default: g.DrawBezier(mp, 120, 152, 125, 159, 135, 159, 140, 152); break;
            }
        }
        g.TranslateTransform(-P.FaceX, -P.FaceY);

        // arms
        var SL = new PointF(64, 150); var SR = new PointF(196, 134);
        if (P.ArmsUp) { Arm(new(64, 130), new(40, 86), -8); Arm(new(196, 130), new(220, 86), 8); }
        else if (P.DanceArm is float da) { Arm(new(64, 130), new(44, 96 - da * 16), -8); Arm(new(196, 130), new(216, 96 + da * 16), 8); }
        else if (P.ArmsHold)
        {
            // both hands up, holding the plate with the step's keys over its head; it presses them one after another
            // (as the chord is pressed), the keys the user holds down light up
            float press = P.KeyDown ? 3 : 0;
            Arm(new(64, 140), new(96, 62 + press), -10, hand: false);
            Arm(new(196, 140), new(164, 62 + press), 10, hand: false);
            DrawKeyPlate(g, inp.KeyChord!.Value, 4 + press, flip, P.KeyPhase, A);
            Hand(96, 62 + press);
            Hand(164, 62 + press);
        }
        else
        {
            // left arm: relaxed, swinging while it walks, or stretched up
            if (P.Stretch is float str) Arm(new(64, 140), new(60 - 20 * str, 150 - 90 * str), -8);
            else Arm(SL, P.Lean ? new PointF(40 + 12 * swing, 178) : new PointF(42, 178), -6);
            // right arm
            if (P.Thumb is float th)
            {
                var H = new PointF(214 - 4 * th, 150 - 56 * th);
                Arm(SR, H, 8);
                Finger(H, new PointF(0, -1), 4, 17);
            }
            else if (P.Loupe)
            {
                var H = new PointF(236, 112);
                Arm(SR, H, 6, hand: false);
                var blue = Palette.Named["blue"];
                using (var hp = new Pen(C(blue), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(hp, H.X + 4, H.Y - 4, H.X + 16, H.Y - 18);
                using (var lf = new SolidBrush(Color.FromArgb(A(46), blue))) g.FillEllipse(lf, H.X + 11, H.Y - 49, 34, 34);
                using (var lp = new Pen(C(blue), 4.5f)) g.DrawEllipse(lp, H.X + 11, H.Y - 49, 34, 34);
                Hand(H.X, H.Y);
            }
            else if (P.Drag is float dq)
            {
                // it carries a glowing dot along the dashed path: the gesture, not only the place
                var A2 = new PointF(206, 186); var B2 = new PointF(262, 112);
                using (var dp = new Pen(Color.FromArgb(A(170), Rim), 2) { DashPattern = new[] { 2.5f, 3f } })
                    g.DrawBezier(dp, A2, new PointF(A2.X + 10, A2.Y - 50), new PointF(B2.X - 30, B2.Y - 10), B2);
                using (var ep2 = new Pen(C(Palette.Accent), 2)) g.DrawEllipse(ep2, B2.X - 10, B2.Y - 10, 20, 20);
                float e = EaseInOut(Math.Min(1, dq / 0.75f)), mt = 1 - e;
                // on the same curve as the path
                float x = mt * mt * mt * A2.X + 3 * mt * mt * e * (A2.X + 10) + 3 * mt * e * e * (B2.X - 30) + e * e * e * B2.X;
                float y = mt * mt * mt * A2.Y + 3 * mt * mt * e * (A2.Y - 50) + 3 * mt * e * e * (B2.Y - 10) + e * e * e * B2.Y;
                var H = new PointF(Math.Min(236, x - 16), Math.Max(120, y + 10));
                Arm(SR, H, 8);
                float fade = dq > 0.85f ? (1 - dq) / 0.15f : 1;
                using (var halo = new SolidBrush(Color.FromArgb((int)(A(90) * fade), Palette.Accent))) g.FillEllipse(halo, x - 13, y - 13, 26, 26);
                using (var dotB = new SolidBrush(Color.FromArgb((int)(A(255) * fade), Palette.Accent))) g.FillEllipse(dotB, x - 8, y - 8, 16, 16);
            }
            else if (P.Scroll is float sc)
            {
                // a finger turning an unseen wheel, arrows running the way to scroll
                float ph = sc * 2.2f % 1;
                var H = new PointF(228, 150 + 10 * MathF.Sin(ph * MathF.PI * 2));
                Arm(SR, H, 8);
                Finger(H, new PointF(1, -0.16f), 3, 15, 5.5f);
                for (int i = 0; i < 3; i++)
                {
                    float q = (sc * 1.4f + i / 3f) % 1;
                    using var ap = new Pen(Color.FromArgb((int)(A(255) * MathF.Sin(q * MathF.PI)), Palette.Accent), 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    if (P.ScrollDir is "left" or "right")
                    {
                        // sideways (a timeline, a wide table): chevrons run along above the hand
                        float d = P.ScrollDir == "right" ? 1 : -1, cy = 112, cx = d > 0 ? 205 + q * 70 : 275 - q * 70;
                        g.DrawLines(ap, new[] { new PointF(cx - 4 * d, cy - 8), new PointF(cx + 4 * d, cy), new PointF(cx - 4 * d, cy + 8) });
                    }
                    else
                    {
                        bool up = P.ScrollDir == "up";
                        float cx = 262, cy = up ? 190 - q * 60 : 130 + q * 60, d = up ? -1 : 1;
                        g.DrawLines(ap, new[] { new PointF(cx - 8, cy - 4 * d), new PointF(cx, cy + 4 * d), new PointF(cx + 8, cy - 4 * d) });
                    }
                }
            }
            else if (P.Point && inp.Target is PointF tp2)
            {
                float dx = (tp2.X - _pos.X) * fx, dy = tp2.Y - (_pos.Y - 6 * k);
                float tap = 0.14f * MathF.Sin(t * MathF.PI * 2 / 1.1f) * P.TapK;
                float ang = MathF.Atan2(dy, dx) + tap - rot * MathF.PI / 180;
                float len = ArmLength + P.Reach;
                var H = new PointF(SR.X + MathF.Cos(ang) * len, SR.Y + MathF.Sin(ang) * len);
                var d = Arm(SR, H, 8, hand: false);
                Finger(H, d, 5, FingerTip);
                Hand(H.X, H.Y);
            }
            else if (P.ArmR is PointF ar) Arm(SR, ar, 8);
            else if (P.Lean) Arm(SR, new PointF(220 - 12 * swing, 178), 6);
            else Arm(SR, new PointF(218, 178), 6);
        }

        // effects around it
        if (P.Sparkle) DrawSparkles(g, t, A);
        if (P.Dots) DrawThinkDots(g, t, A);
        if (P.Zz)
        {
            for (int i = 0; i < 3; i++)
            {
                float q = (t * 0.45f + i / 3f) % 1;
                var font = Theme.Font(14 + q * 12, bold: true);
                using var zb = new SolidBrush(Color.FromArgb((int)(A(230) * MathF.Sin(q * MathF.PI)), Rim));
                var st = g.Save();
                g.TranslateTransform(196 + q * 34 + i * 2, 70 - q * 60);
                if (flip) g.ScaleTransform(-1, 1);     // letters read the right way round when the figure is mirrored
                g.DrawString("z", font, zb, 0, -font.GetHeight());
                g.Restore(st);
            }
        }
        if (P.Confetti is float cf)
        {
            var cols = new[] { Palette.Accent, Palette.Success, Palette.Warn, Palette.Named["blue"], Palette.Named["purple"] };
            for (int i = 0; i < 22; i++)
            {
                float q = (cf * 0.35f + i * 0.137f) % 1;
                float x = 10 + (i * 53 % 260) + MathF.Sin(q * 6 + i) * 10, y = -40 + q * 240;
                using var b = new SolidBrush(Color.FromArgb((int)(A(255) * (q < 0.85f ? 1 : (1 - q) / 0.15f)), cols[i % 5]));
                var st = g.Save();
                g.TranslateTransform(x, y);
                g.RotateTransform((q * 8 + i) * 180 / MathF.PI);
                g.FillRectangle(b, -4, -2, 8, 4);
                g.Restore(st);
            }
        }
        g.Restore(state);
    }

    private static readonly Font KeyFont = new("Consolas", 25, FontStyle.Bold, GraphicsUnit.Pixel);
    private const float KeyCycle = 2.2f;          // s: press the keys one by one, hold the chord, let go, rest

    /// <summary>The chord's keys in pressing order: modifiers first, then the key.</summary>
    internal static string[] CapLabels(Chord c) =>
        TutorialOverlay.Keys.FormatMods(c.Mods).Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Append(TutorialOverlay.Keys.KeyName(c.Vk)).ToArray();

    private static float CapDownAt(int i) => 0.25f + i * 0.22f;     // when key i goes down in the cycle
    private static float AllUpAt(int n) => CapDownAt(n - 1) + 0.45f;

    /// <summary>Is the user holding this key of the chord down right now?</summary>
    private static bool Held(Chord c, int i, int n)
    {
        if (i == n - 1) return (Native.GetAsyncKeyState((int)c.Vk) & 0x8000) != 0;
        var mods = new List<int>();
        if ((c.Mods & Native.MOD_CONTROL) != 0) mods.Add(0x11);
        if ((c.Mods & Native.MOD_ALT) != 0) mods.Add(0x12);
        if ((c.Mods & Native.MOD_SHIFT) != 0) mods.Add(0x10);
        if ((c.Mods & Native.MOD_WIN) != 0) mods.Add(0x5B);
        if (i >= mods.Count) return false;
        bool down = (Native.GetAsyncKeyState(mods[i]) & 0x8000) != 0;
        if (mods[i] == 0x5B) down |= (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0;
        return down;
    }

    /// <summary>
    /// The plate over its head, in design units from <paramref name="top"/>: one key cap per key of the chord
    /// (Ctrl, Shift, P), pressed one after another the way the chord is pressed. A key the user holds down lights up,
    /// so what is still missing shows. Never wider than the hands' reach (240); drawn the right way round when the
    /// figure is mirrored.
    /// </summary>
    private static void DrawKeyPlate(Graphics g, Chord chord, float top, bool flip, float phase, Func<int, int> a)
    {
        var labels = CapLabels(chord);
        int n = labels.Length;
        const float capH = 38, side = 5, gap = 6, pad = 7;
        var widths = labels.Select(l => Math.Max(capH, g.MeasureString(l, KeyFont).Width + 10)).ToArray();
        float inner = widths.Sum() + gap * (n - 1), plateW = inner + 2 * pad, plateH = capH + side + 2 * pad;
        float fit = Math.Min(1, 240 / plateW);

        var st = g.Save();
        if (flip) { g.TranslateTransform(260, 0); g.ScaleTransform(-1, 1); }
        g.TranslateTransform(130, top);
        g.ScaleTransform(fit, fit);
        var plate = new RectangleF(-plateW / 2, 0, plateW, plateH);
        using (var path = Theme.Rounded(plate, 9))
        {
            using (var b = new SolidBrush(Color.FromArgb(a(255), Track))) g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb(a(255), Palette.Accent), 2.5f);
            g.DrawPath(pen, path);
        }
        using var sideB = new SolidBrush(Color.FromArgb(a(255), Rim));
        using var faceB = new SolidBrush(Color.FromArgb(a(255), 240, 242, 247));
        using var litB = new SolidBrush(Color.FromArgb(a(255), Palette.Accent));
        using var inkB = new SolidBrush(Color.FromArgb(a(255), 32, 35, 44));
        using var litInk = new SolidBrush(Color.FromArgb(a(255), 255, 255, 255));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        float x = -inner / 2;
        for (int i = 0; i < n; i++)
        {
            bool held = Held(chord, i, n);
            bool down = held || (phase >= CapDownAt(i) && phase < AllUpAt(n));
            float dy = down ? side - 1 : 0;
            var face = new RectangleF(x, pad + dy, widths[i], capH);
            using (var sp = Theme.Rounded(new RectangleF(x, pad + side, widths[i], capH), 7)) g.FillPath(sideB, sp);
            using (var fp = Theme.Rounded(face, 7)) g.FillPath(held ? litB : faceB, fp);
            g.DrawString(labels[i], KeyFont, held ? litInk : inkB, face, sf);
            x += widths[i] + gap;
        }
        g.Restore(st);
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
        Native.PresentLayered(Handle, bmp, rect);
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
