using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace TutorialOverlay;

/// <summary>
/// Strokes, held keys and the wheel: "trace" (drag along a path), "hold" on drag/trace (keys down the whole
/// time) and "scroll" (wheel notches over an area, or until the step's targets come into view).
/// Moves and wheel turns are read only while such a step is current, and only the outcome is reported.
/// </summary>
internal static partial class Engine
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Milliseconds since the user last touched any input device (touchpad included) — not what they did.</summary>
    private static int IdleMs()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? Environment.TickCount - (int)li.dwTime : int.MaxValue;
    }

    private static bool WantsMotion(Step? step) =>
        step != null && step.Until.Any(c => !c.Met && (c.Type is "trace" or "scroll" || (c.Type == "drag" && c.Hold.Count > 0)));

    // the hook's own record first; GetAsyncKeyState as a second opinion
    private static bool AllHeld(List<uint> keys) => keys.All(k => InputHook.IsHeld(k) || GetAsyncKeyState((int)k) < 0);

    /// <summary>The keys the current step needs held, for the keyboard hook to track.</summary>
    private static uint[] HoldKeys(Step? step) =>
        step == null ? Array.Empty<uint>() : step.Until.Where(c => !c.Met && c.Hold.Count > 0).SelectMany(c => c.Hold).Distinct().ToArray();

    private static Step? MotionStep()
    {
        var t = _tut;
        return t is { State: TutorialState.Running, AdvanceAt: null } ? t.Current : null;
    }

    // =========================================================== the path on screen

    /// <summary>
    /// The trace's path in screen px, densified to points ~4 px apart (a Catmull-Rom curve through the given
    /// points when smooth), so "how far along" is just an index. Null while its anchor is not resolved.
    /// </summary>
    private static List<PointF>? PathOnScreen(Condition c, out float scale)
    {
        scale = 1;
        var ag = c.Anchor == null || c.Anchor.IsScreen ? new AnchorGeo { P = PointF.Empty, Scale = Native.ScaleAt(c.Path[0]) } : AnchorNow(c.Anchor, out _);
        if (ag == null) return null;
        scale = ag.Scale;
        var pts = c.Path.Select(p => new PointF(ag.P.X + p.X, ag.P.Y + p.Y)).ToList();
        return Densify(pts, c.Smooth, 4);
    }

    public static List<PointF> Densify(List<PointF> pts, bool smooth, float step)
    {
        var curve = new List<PointF>();
        if (pts.Count < 2) return pts.ToList();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            PointF p0 = pts[Math.Max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            float len = MathF.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
            int n = Math.Max(1, (int)(len / step));
            for (int k = 0; k < n; k++)
            {
                float t = k / (float)n;
                curve.Add(smooth ? CatmullRom(p0, p1, p2, p3, t) : new PointF(p1.X + (p2.X - p1.X) * t, p1.Y + (p2.Y - p1.Y) * t));
            }
        }
        curve.Add(pts[^1]);
        return curve;
    }

    private static PointF CatmullRom(PointF p0, PointF p1, PointF p2, PointF p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        float X(float a, float b, float c, float d) => 0.5f * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3);
        return new PointF(X(p0.X, p1.X, p2.X, p3.X), X(p0.Y, p1.Y, p2.Y, p3.Y));
    }

    private static (int index, float dist) Nearest(List<PointF> path, PointF p, int from = 0, int to = int.MaxValue)
    {
        int best = -1;
        float bd = float.MaxValue;
        for (int i = Math.Max(0, from); i < Math.Min(path.Count, to); i++)
        {
            float d = (path[i].X - p.X) * (path[i].X - p.X) + (path[i].Y - p.Y) * (path[i].Y - p.Y);
            if (d < bd) { bd = d; best = i; }
        }
        return (best, MathF.Sqrt(bd));
    }

    private static float TraceProgress(Condition c, int count) =>
        count < 2 ? 0 : Math.Clamp((c.TraceMax - c.TraceFrom) / (float)(count - 1), 0, 1);

    // =========================================================== input

    private static void MotionDown(PointF p, string button)
    {
        lock (Sync)
        {
            var step = MotionStep();
            if (step == null) return;
            foreach (var c in step.Until.Where(c => !c.Met && c.Type is "trace" or "drag"))
            {
                if (c.Button != "any" && c.Button != button) continue;
                c.HoldBroken = c.Hold.Count > 0 && !AllHeld(c.Hold);
                if (c.Type != "trace") continue;
                var path = PathOnScreen(c, out float s);
                if (path == null) continue;
                float tol = c.Tolerance * s;
                // ordered: start at the beginning; otherwise anywhere on the path
                var (i, d) = Nearest(path, p, 0, c.Ordered ? Math.Max(2, path.Count / 6) : int.MaxValue);
                if (d > tol * 1.5f)
                {
                    if (c.Ordered && Nearest(path, p).dist <= tol) Fail(c, "start_at_the_beginning", 0);
                    continue;
                }
                c.TraceActive = true;
                c.TraceFailed = false;
                c.FailReason = null;
                c.TraceFrom = c.Ordered ? 0 : i;
                c.TraceMax = i;
                if (c.HoldBroken) Fail(c, "key_not_held", 0);
            }
        }
    }

    private static void MotionMove(PointF p)
    {
        lock (Sync)
        {
            var step = MotionStep();
            if (step == null) return;
            foreach (var c in step.Until.Where(c => !c.Met && c.Type is "trace" or "drag"))
            {
                if (c.Hold.Count > 0 && !c.HoldBroken && !AllHeld(c.Hold))
                {
                    c.HoldBroken = true;
                    if (c.TraceActive && !c.TraceFailed) Fail(c, "released_key", Progress(c));
                }
                if (c.Type != "trace" || !c.TraceActive || c.TraceFailed) continue;
                var path = PathOnScreen(c, out float s);
                if (path == null) continue;
                // ordered: look a little behind and ahead of the progress so far — never jump to the far end
                var (i, d) = c.Ordered ? Nearest(path, p, c.TraceMax - 12, c.TraceMax + 60) : Nearest(path, p);
                if (d > c.Tolerance * s) { Fail(c, "off_path", TraceProgress(c, path.Count)); continue; }
                if (c.Ordered) c.TraceMax = Math.Max(c.TraceMax, i);
                else { c.TraceFrom = Math.Min(c.TraceFrom, i); c.TraceMax = Math.Max(c.TraceMax, i); }
            }
        }
    }

    private static void MotionUp(PointF p, string button)
    {
        lock (Sync)
        {
            var t = _tut;
            var step = MotionStep();
            if (step == null) return;
            foreach (var c in step.Until.Where(c => !c.Met && c.Type == "trace" && c.TraceActive))
            {
                if (c.Button != "any" && c.Button != button) continue;
                c.TraceActive = false;
                if (c.TraceFailed) continue;
                var path = PathOnScreen(c, out _);
                float prog = path == null ? 0 : TraceProgress(c, path.Count);
                if (c.Hold.Count > 0 && c.HoldBroken) { Fail(c, "released_key", prog); continue; }
                if (prog >= c.Coverage)
                {
                    c.Met = true;
                    Events.Emit("trace_done", new() { ["step"] = t!.Index, ["progress"] = Math.Round(prog, 2), ["hold"] = c.HoldNames.Count > 0 ? string.Join("+", c.HoldNames) : null });
                }
                else Fail(c, "too_short", prog);
            }
        }
    }

    private static float Progress(Condition c) => PathOnScreen(c, out _) is { } path ? TraceProgress(c, path.Count) : 0;

    /// <summary>One report per failed attempt: why, and how far along it got (never the mouse path itself).</summary>
    private static void Fail(Condition c, string reason, float progress)
    {
        if (c.TraceFailed) return;
        c.TraceFailed = true;
        c.FailReason = reason;
        c.FailedAt = DateTime.UtcNow;
        Events.Emit("trace_off", new()
        {
            ["step"] = _tut?.Index, ["reason"] = reason, ["progress"] = Math.Round(progress, 2),
            ["hold"] = c.HoldNames.Count > 0 ? string.Join("+", c.HoldNames) : null,
        });
    }

    /// <summary>A drag that needed keys held: did they stay down?</summary>
    private static bool DragHoldOk(Condition c) => c.Hold.Count == 0 || (!c.HoldBroken && AllHeld(c.Hold));

    private static void MotionWheel(InputEvent e)
    {
        lock (Sync)
        {
            var t = _tut;
            var step = MotionStep();
            if (step == null) return;
            var p = new PointF(e.X, e.Y);
            int fgPid = ForegroundPid();
            foreach (var c in step.Until.Where(c => !c.Met && c.Type == "scroll"))
            {
                bool outside = c.Area != null && !InRegion(c.Area, p);
                ScrollDebug($"wheel {e.Button} {e.Delta} at {e.X},{e.Y}" + (outside ? " (outside the area)" : ""));
                if (outside)
                {
                    // scrolling the app, but another pane (a piano roll instead of the browser): say where. Scrolling
                    // in another program is none of our business
                    var proc = c.Area!.Anchor.Process ?? t!.Defaults.Process;
                    if (proc == null || ProcName(fgPid).Equals(proc, StringComparison.OrdinalIgnoreCase)) c.OffAreaAt = DateTime.UtcNow;
                    continue;
                }
                if (c.Area == null && t!.Defaults.Process != null && !ProcName(fgPid).Equals(t.Defaults.Process, StringComparison.OrdinalIgnoreCase)) continue;
                // keys the step asks to hold while scrolling (Ctrl to zoom): a turn without them does not count
                if (c.Hold.Count > 0 && !AllHeld(c.Hold)) continue;
                // wheel forward (delta > 0) scrolls up; tilt right (delta > 0) scrolls right. Most mice have no tilt:
                // sideways is Shift + the wheel (down = right), as in FL's playlist, browsers, editors
                bool sideways = c.Direction is "left" or "right";
                bool shiftWheel = e.Button == "v" && sideways && (InputHook.IsHeld(0x10) || GetAsyncKeyState(0x10) < 0);
                string dir = e.Button == "v" ? (shiftWheel ? (e.Delta > 0 ? "left" : "right") : e.Delta > 0 ? "up" : "down")
                                             : (e.Delta > 0 ? "right" : "left");
                if (dir != c.Direction) continue;
                // a mouse wheel sends 120 per notch, a precision touchpad many small deltas: add them up
                c.ScrollAccum += Math.Abs(e.Delta);
                c.ScrollCount = Math.Max(c.ScrollAccum / 120, (int)(c.ScrollPx / (60 * Native.ScaleAt(p))));
                if (!c.UntilVisible && c.ScrollCount >= c.Notches)
                {
                    c.Met = true;
                    Events.Emit("scroll_done", new() { ["step"] = t!.Index, ["direction"] = dir, ["notches"] = c.ScrollCount });
                }
            }
        }
    }

    // =========================================================== per frame (under Sync)

    /// <summary>The path to follow (dashed, with the part done so far solid), held-key chips and the wheel hint.</summary>
    /// <summary>
    /// A step that scrolls, with the mascot on: the area to scroll is framed and the mascot scrolls in it, the way
    /// the step wants. No wheel pictures: the step's own and the target's two-way "somewhere here" wheel only
    /// contradicted each other, and a tilt wheel most mice do not have was no help at all.
    /// </summary>
    internal static bool MascotLeadsScroll(Tutorial t, Step step) =>
        t.Mascot && Settings.Current.Mascot && step.Until.Any(c => c.Type == "scroll" && !c.Met && c.Area != null);

    private static void AddMotionVisuals(Step step, Scene sc, List<RectangleF> avoid, int fgPid)
    {
        bool hideInactive = _tut?.HideWhenInactive ?? HideWhenInactiveDefault;
        foreach (var c in step.Until.Where(c => c.Show && !c.Met))
        {
            if (c.Type == "trace")
            {
                var path = PathOnScreen(c, out float s);
                if (path == null) continue;
                if (hideInactive && c.Anchor is { IsScreen: false } a && !AppActive(Array.Empty<int>(), a.Process, fgPid)) continue;
                bool failedLately = c.TraceFailed && (DateTime.UtcNow - c.FailedAt).TotalSeconds < 1.2;
                var bounds = PathBounds(path, 14 * s);
                var prim = new Prim
                {
                    Kind = PrimKind.Path, Points = path.ToArray(), Rect = bounds, Width = 4 * s, Scale = s,
                    Color = failedLately ? Palette.Warn : Palette.Accent,
                    Progress = c.TraceActive && !c.TraceFailed ? TraceProgress(c, path.Count) : 0,
                };
                sc.Prims.Add((prim, 1f));
                avoid.Add(bounds);
                if (c.Hold.Count > 0) AddHoldChip(c, path[0], s, sc, avoid);
            }
            else if (c.Type == "drag" && c.Hold.Count > 0 && RegionGeo(c.From!) is { } fg)
                AddHoldChip(c, fg.c, fg.ag.Scale, sc, avoid);
            else if (c.Type == "scroll")
            {
                PointF at;
                float s;
                if (c.Area != null && RegionGeo(c.Area) is { } g)
                {
                    at = g.circle ? new PointF(g.c.X + g.r + 30 * g.ag.Scale, g.c.Y) : new PointF(g.rect.Right - 34 * g.ag.Scale, g.rect.Y + g.rect.Height / 2);
                    s = g.ag.Scale;
                    if (hideInactive && !c.Area.Anchor.IsScreen && !AppActive(g.ag.Pids, c.Area.Anchor.Process, fgPid)) continue;
                }
                else continue;
                var box = Prim.WheelBox(at, s);
                if (RegionGeo(c.Area!) is { } area)
                {
                    var areaRect = area.circle ? new RectangleF(area.c.X - area.r, area.c.Y - area.r, 2 * area.r, 2 * area.r) : area.rect;
                    WatchScroll(c, areaRect, area.ag, s);
                    // where to scroll: a thin frame round the area (the wheel alone, at its edge, is easy to miss)
                    var frame = RectangleF.Inflate(areaRect, -2 * s, -2 * s);
                    sc.Prims.Add((new Prim { Kind = PrimKind.Rect, Rect = frame, Color = Palette.Accent, Width = 2 * s, Radius = 6 * s, Scale = s, Glow = false }, 1f));
                }
                if (_tut != null && MascotLeadsScroll(_tut, step)) continue;   // the frame and the mascot show it, no wheel
                string text = c.UntilVisible ? "" : $"{Math.Min(c.ScrollCount, c.Notches)}/{c.Notches}";
                sc.Prims.Add((new Prim { Kind = PrimKind.Wheel, A = at, Rect = box, Text = c.Direction + "|" + text, Color = Palette.Accent, Scale = s }, 1f));
                avoid.Add(box);
            }
        }

        // "until visible": done as soon as the step's targets show after some scrolling
        // "until visible": the targets were off screen and now show — after wheel turns, or after any scrolling we
        // could not see (apps that take touchpad scrolling directly, without wheel messages)
        foreach (var c in step.Until.Where(c => c.Type == "scroll" && c.UntilVisible && !c.Met))
        {
            bool visible = step.Required.Any() && step.Required.All(m => m.Geo != null);
            if (!visible) { c.SeenHidden = true; continue; }
            if (c.ScrollAccum > 0 || c.SeenHidden)
            {
                c.Met = true;
                Events.Emit("scroll_done", new() { ["step"] = _tut?.Index, ["direction"] = c.Direction, ["notches"] = c.ScrollCount, ["visible"] = true });
            }
        }
    }

    // =========================================================== scrolling, seen in the pixels

    private const int ScrollSampleMs = 120;

    /// <summary>
    /// Wheel messages do not tell the whole story: browsers and other apps take touchpad scrolling directly, and a
    /// scrollbar drag sends none. So the area itself is watched (its window's own pixels, never our drawings,
    /// ~8 times a second, off the UI thread): when its content moves up, the user scrolled down, and so on.
    /// The step counts whichever is more — wheel notches or the content's travel (~60 px a notch at 100 %).
    /// </summary>
    private static void WatchScroll(Condition c, RectangleF area, AnchorGeo ag, float s)
    {
        var now = DateTime.UtcNow;
        if (c.ScrollSampling || (now - c.ScrollSampledAt).TotalMilliseconds < ScrollSampleMs) return;
        c.ScrollSampling = true;
        c.ScrollSampledAt = now;
        var rect = Rectangle.Round(area);
        // the area's own window: the anchor's, or (a screen area) whatever app window is under it
        var root = ag.Root != IntPtr.Zero ? ag.Root : Native.HitWindow(new PointF(area.X + area.Width / 2, area.Y + area.Height / 2)).root;
        bool vertical = c.Direction is "up" or "down";
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (root == IntPtr.Zero || rect.Width < 16 || rect.Height < 16) return;
                using var shot = ScreenCapture.CaptureWindow(root, rect);
                if (shot == null) { ScrollDebug("no picture of the area (minimised, or the window prints blank)"); return; }
                var gray = Template.Gray(shot, out int w, out int h);     // half size
                byte[]? prev;
                Size prevSize;
                lock (Sync) { prev = c.ScrollPrev; prevSize = c.ScrollPrevSize; c.ScrollPrev = gray; c.ScrollPrevSize = new Size(w, h); }
                if (prev == null || prevSize.Width != w || prevSize.Height != h) return;
                int shift = ContentShift(prev, gray, w, h, vertical, out double still, out double bestDiff);
                if (shift == 0) { if (still >= 2) ScrollDebug($"content changed but no clear shift (still {still:0.0}, best {bestDiff:0.0})"); return; }
                // only the user's own scrolling: the pointer over the area (Windows scrolls what is under it) and
                // input from them just now — a chat, a log or a video moving by itself has neither
                Native.GetCursorPos(out var cur);
                if (!rect.Contains(cur.X, cur.Y) || IdleMs() > 400)
                {
                    ScrollDebug($"shift {shift} not counted: pointer {(rect.Contains(cur.X, cur.Y) ? "over" : "outside")} the area {rect}, last input {IdleMs()} ms ago");
                    return;
                }
                // content moving up (negative) = scrolled down; moving left = scrolled right
                string dir = vertical ? (shift < 0 ? "down" : "up") : (shift < 0 ? "right" : "left");
                lock (Sync)
                {
                    if (c.Met || dir != c.Direction) return;
                    c.ScrollPx += Math.Abs(shift) * 2;
                    c.ScrollCount = Math.Max(c.ScrollAccum / 120, (int)(c.ScrollPx / (60 * s)));
                    if (!c.UntilVisible && c.ScrollCount >= c.Notches)
                    {
                        c.Met = true;
                        Events.Emit("scroll_done", new() { ["step"] = _tut?.Index, ["direction"] = dir, ["notches"] = c.ScrollCount, ["seen"] = "content" });
                    }
                }
            }
            catch (Exception e) { Log.Debug("scroll watch: " + e.Message); }
            finally { lock (Sync) c.ScrollSampling = false; }
        });
    }

    /// <summary>
    /// How far the picture moved between two looks (half-size px; 0 = not moved or not a scroll). The best shift
    /// must match clearly better than "no shift", or it is something else changing (an animation, a hover).
    /// </summary>
    private static DateTime _scrollDebugAt;

    /// <summary>Why content scrolling was not counted (--verbose; at most once a second).</summary>
    private static void ScrollDebug(string why)
    {
        if ((DateTime.UtcNow - _scrollDebugAt).TotalSeconds < 1) return;
        _scrollDebugAt = DateTime.UtcNow;
        Log.Debug("scroll watch: " + why);
    }

    /// <summary>
    /// The area is often a whole window whose parts do not all scroll (a settings window: its list of sections
    /// stays, the page beside it moves), and one shift for all of it never fits clearly. So it is cut into strips
    /// across the scroll direction (columns when scrolling up and down) and each strip is judged on its own: the
    /// strip that moved most clearly gives the shift.
    /// </summary>
    internal static int ContentShift(byte[] a, byte[] b, int w, int h, bool vertical, out double still, out double bestDiff)
    {
        const int Strips = 4;
        int across = vertical ? w : h;
        still = 0; bestDiff = double.MaxValue;
        int shift = 0; double bestRatio = 1;
        for (int i = 0; i < Strips; i++)
        {
            int from = across * i / Strips, to = across * (i + 1) / Strips;
            int sh = StripShift(a, b, w, h, vertical, from, to, out double st, out double bd);
            still = Math.Max(still, st);
            if (sh == 0) { bestDiff = Math.Min(bestDiff, bd); continue; }
            double ratio = bd / Math.Max(0.01, st);
            if (ratio < bestRatio) { bestRatio = ratio; shift = sh; bestDiff = bd; }
        }
        return shift;
    }

    /// <summary>One strip (from..to across the scroll direction): how far it moved, 0 if not clearly.</summary>
    private static int StripShift(byte[] a, byte[] b, int w, int h, bool vertical, int from, int to, out double still, out double bestDiff)
    {
        int len = vertical ? h : w, max = len / 3;
        double Diff(int d)
        {
            long sum = 0, n = 0;
            if (vertical)
                for (int y = Math.Max(0, d); y < Math.Min(h, h + d); y += 2)
                    for (int x = from; x < to; x += 2) { sum += Math.Abs(a[(y - d) * w + x] - b[y * w + x]); n++; }
            else
                for (int y = from; y < to; y += 2)
                    for (int x = Math.Max(0, d); x < Math.Min(w, w + d); x += 2) { sum += Math.Abs(a[y * w + x - d] - b[y * w + x]); n++; }
            return n == 0 ? double.MaxValue : sum / (double)n;
        }
        still = Diff(0);
        bestDiff = still;
        if (still < 2) return 0;                      // nothing changed here
        int best = 0;
        for (int d = -max; d <= max; d++)
        {
            if (d == 0) continue;
            double v = Diff(d);
            if (v < bestDiff) { bestDiff = v; best = d; }
        }
        return bestDiff < still * 0.5 && bestDiff < 12 ? best : 0;
    }

    /// <summary>"Hold Shift" next to where the stroke starts; lit while the keys are down.</summary>
    private static void AddHoldChip(Condition c, PointF at, float s, Scene sc, List<RectangleF> avoid)
    {
        bool down = AllHeld(c.Hold);
        var loc = Settings.Current.Text;
        string text = (down ? "✓ " : "") + string.Format(loc.HoldKeys, string.Join(" + ", c.HoldNames));
        float px = 13 * s;
        var size = Theme.Measure(text, Theme.Font(px, true), 300 * s);
        var box = new RectangleF(at.X - size.Width / 2 - 14 * s, at.Y - 52 * s - size.Height, size.Width + 28 * s, size.Height + 14 * s);
        sc.Prims.Add((new Prim { Kind = PrimKind.Label, Rect = box, Text = text, Size = px, Color = down ? Palette.Success : Palette.Warn, Scale = s, Radius = 8 * s }, 1f));
        avoid.Add(box);
    }

    private static RectangleF PathBounds(List<PointF> path, float pad)
    {
        float l = path.Min(p => p.X), t = path.Min(p => p.Y), r = path.Max(p => p.X), b = path.Max(p => p.Y);
        return RectangleF.FromLTRB(l - pad, t - pad, r + pad, b + pad);
    }

    /// <summary>Where the mascot points on a stroke or scroll step without targets: the start of the path / the wheel.</summary>
    /// <summary>
    /// Where a stroke or scroll step happens, for the mascot: the point to show, what it must not cover there (the
    /// whole wheel hint with its counter, not just a point), and for a scroll step the condition itself.
    /// </summary>
    private static (PointF at, RectangleF keepClear, Condition? scroll)? MotionFocus(Step step)
    {
        foreach (var c in step.Until.Where(c => !c.Met))
        {
            if (c.Type == "trace" && PathOnScreen(c, out _) is { Count: > 0 } path)
                return (path[0], new RectangleF(path[0].X - 14, path[0].Y - 14, 28, 28), null);
            if (c.Type == "scroll" && c.Area != null && RegionGeo(c.Area) is { } g)
            {
                float s = g.ag.Scale;
                var at = g.circle ? new PointF(g.c.X + g.r + 30 * s, g.c.Y) : new PointF(g.rect.Right - 34 * s, g.rect.Y + g.rect.Height / 2);
                return (at, Prim.WheelBox(at, s), c);
            }
        }
        return null;
    }
}
