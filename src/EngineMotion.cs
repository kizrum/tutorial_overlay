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
        return t is { State: "running", AdvanceAt: null } ? t.Current : null;
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
                if (c.Area != null) { if (!InRegion(c.Area, p)) continue; }
                else if (t!.Defaults.Process != null && !ProcName(fgPid).Equals(t.Defaults.Process, StringComparison.OrdinalIgnoreCase)) continue;
                // wheel forward (delta > 0) scrolls up; tilt right (delta > 0) scrolls right
                string dir = e.Button == "v" ? (e.Delta > 0 ? "up" : "down") : (e.Delta > 0 ? "right" : "left");
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
                var box = new RectangleF(at.X - 20 * s, at.Y - 34 * s, 40 * s, 68 * s);
                if (RegionGeo(c.Area!) is { } area) WatchScroll(c, area.circle ? new RectangleF(area.c.X - area.r, area.c.Y - area.r, 2 * area.r, 2 * area.r) : area.rect, area.ag, s);
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
                if (shot == null) return;
                var gray = Template.Gray(shot, out int w, out int h);     // half size
                byte[]? prev;
                Size prevSize;
                lock (Sync) { prev = c.ScrollPrev; prevSize = c.ScrollPrevSize; c.ScrollPrev = gray; c.ScrollPrevSize = new Size(w, h); }
                if (prev == null || prevSize.Width != w || prevSize.Height != h) return;
                int shift = ContentShift(prev, gray, w, h, vertical);
                if (shift == 0) return;
                // only the user's own scrolling: the pointer over the area (Windows scrolls what is under it) and
                // input from them just now — a chat, a log or a video moving by itself has neither
                Native.GetCursorPos(out var cur);
                if (!rect.Contains(cur.X, cur.Y) || IdleMs() > 400) return;
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
    private static int ContentShift(byte[] a, byte[] b, int w, int h, bool vertical)
    {
        int len = vertical ? h : w, max = len / 3;
        double Diff(int d)
        {
            long sum = 0, n = 0;
            if (vertical)
                for (int y = Math.Max(0, d); y < Math.Min(h, h + d); y += 2)
                    for (int x = 0; x < w; x += 2) { sum += Math.Abs(a[(y - d) * w + x] - b[y * w + x]); n++; }
            else
                for (int y = 0; y < h; y += 2)
                    for (int x = Math.Max(0, d); x < Math.Min(w, w + d); x += 2) { sum += Math.Abs(a[y * w + x - d] - b[y * w + x]); n++; }
            return n == 0 ? double.MaxValue : sum / (double)n;
        }
        double still = Diff(0);
        if (still < 2) return 0;                      // nothing changed
        int best = 0;
        double bestDiff = still;
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
    private static PointF? MotionFocus(Step step)
    {
        foreach (var c in step.Until.Where(c => !c.Met))
        {
            if (c.Type == "trace" && PathOnScreen(c, out _) is { Count: > 0 } path) return path[0];
            if (c.Type == "scroll" && c.Area != null && RegionGeo(c.Area) is { } g) return g.circle ? g.c : new PointF(g.rect.Right - 34 * g.ag.Scale, g.rect.Y + g.rect.Height / 2);
        }
        return null;
    }
}
