using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

internal sealed record CardButton(string Id, string Text, string Style, string? Action);

internal sealed class CardModel
{
    public bool Visible;
    public string State = "";
    public int Index, Total;
    public string Title = "", Text = "";
    public int Done, Required;
    public bool Completed;
    public bool CanBack, CanUndo;
    public List<(string label, bool done)> Checklist = new();
    public List<CardButton> Extra = new();
    public HashSet<string> Controls = new();
    public List<RectangleF> Avoid = new();
    public PointF Focus;                 // where the user is looking (picks the monitor)
    public RectangleF? AppFrame;
    public int StepVersion;
}

/// <summary>
/// All mutable state lives here behind one lock. Tool calls, the click consumer and the UI tick all take it;
/// the UI thread then renders from the snapshot it built.
/// </summary>
internal static class Engine
{
    public static readonly object Sync = new();
    public static bool Animate = true;
    public static bool HideWhenInactiveDefault = true;

    private static readonly List<Mark> Free = new();
    private static Tutorial? _tut;
    private static readonly List<Ripple> Ripples = new();
    private static int _freeCounter;
    private static int _stepVersion;
    private static DateTime _lastPixel, _lastCond;
    private static readonly int OwnPid = Environment.ProcessId;
    private static readonly Dictionary<int, string> ProcNames = new();
    private static string _lastSignature = "";

    public static Tutorial? Tutorial => _tut;

    // =========================================================== free marks (draw / clear)

    public static List<string> Draw(JsonNode args)
    {
        lock (Sync)
        {
            var ids = new List<string>();
            var defaults = J.Has(args, "anchor") ? AnchorSpec.Parse(J.Get(args, "anchor"), _tut?.Defaults) : _tut?.Defaults;
            var markNodes = J.Items(J.Get(args, "marks")).ToList();
            if (markNodes.Count == 0) markNodes.Add(args);
            foreach (var n in markNodes)
            {
                var m = Mark.Parse(n, defaults, J.Str(n, "id") ?? $"m{++_freeCounter}");
                if (!J.Has(n, "anchor") && defaults != null) m.Anchor = defaults;
                if (!J.Has(n, "dismissOnClick") && J.Bool(args, "dismissOnClick") is bool d) m.DismissOnClick = d;
                if ((J.Bool(n, "hideWhenInactive") ?? J.Bool(args, "hideWhenInactive")) is bool h) m.HideInactive = h;
                Free.RemoveAll(x => x.Id == m.Id);
                Free.Add(m);
                ids.Add(m.Id);
            }
            if (J.Bool(args, "replace") == true) Free.RemoveAll(x => !ids.Contains(x.Id));
            return ids;
        }
    }

    public static int Clear(string? id)
    {
        lock (Sync)
        {
            int n = id == null ? Free.Count : Free.RemoveAll(m => m.Id == id || m.Id.StartsWith(id + "/"));
            if (id == null) Free.Clear();
            return n;
        }
    }

    // =========================================================== tutorial control

    public static JsonObject StartTutorial(JsonNode spec)
    {
        lock (Sync)
        {
            spec = J.Unwrap(J.Get(spec, "spec")) ?? spec;
            var t = new Tutorial
            {
                Title = J.Str(spec, "title") ?? "",
                KeepOpen = J.Bool(spec, "keepOpen") ?? false,
                Toggle = J.Bool(spec, "toggle") ?? false,
                Dim = J.Num(spec, "dim") is double d ? (float)d : null,
                AdvanceDelayMs = J.Int(spec, "advanceDelayMs") ?? 450,
                HideWhenInactive = J.Bool(spec, "hideWhenInactive") ?? HideWhenInactiveDefault,
            };
            t.Defaults = AnchorSpec.Parse(J.Get(spec, "anchor") ?? new JsonObject { ["process"] = J.Str(spec, "app") ?? J.Str(spec, "process") }, null);
            if (J.Str(spec, "window") is string w && t.Defaults.Window == null) t.Defaults.Window = w;
            int i = 0;
            foreach (var s in J.Items(J.Get(spec, "steps"))) t.Steps.Add(Step.Parse(s, t.Defaults, i++));
            if (_tut is { Active: true }) Events.Emit("tutorial_stopped", new() { ["title"] = _tut.Title, ["reason"] = "replaced" });
            _tut = t;
            Events.Emit("tutorial_started", new() { ["title"] = t.Title, ["total"] = t.Steps.Count, ["app"] = t.Defaults.Process });
            int start = Math.Clamp(J.Int(spec, "startAt") ?? 0, 0, Math.Max(0, t.Steps.Count - 1));
            if (t.Steps.Count == 0) Finish(); else Enter(start);
            return TutorialJson();
        }
    }

    public static JsonObject AppendSteps(JsonNode args)
    {
        lock (Sync)
        {
            if (_tut == null || !_tut.Active) throw new InvalidOperationException("no active tutorial (start_tutorial first)");
            int i = _tut.Steps.Count, added = 0;
            int? insertAt = J.Int(args, "insertAt");
            var parsed = J.Items(J.Get(args, "steps") ?? args).Select(s => Step.Parse(s, _tut.Defaults, i++)).ToList();
            if (insertAt is int at && at > _tut.Index && at <= _tut.Steps.Count) _tut.Steps.InsertRange(at, parsed);
            else _tut.Steps.AddRange(parsed);
            added = parsed.Count;
            Events.Emit("steps_appended", new() { ["added"] = added, ["total"] = _tut.Steps.Count });
            if (_tut.State == "waiting" && added > 0) { _tut.State = "running"; Enter(_tut.Index + 1); }
            _stepVersion++;
            return TutorialJson();
        }
    }

    public static JsonObject GotoStep(JsonNode args)
    {
        lock (Sync)
        {
            var t = Require();
            int idx = J.Str(args, "id") is string id ? t.Steps.FindIndex(s => s.Id == id) : J.Int(args, "index") ?? -1;
            if (J.Str(args, "relative") is string rel) idx = t.Index + (rel == "back" || rel == "prev" ? -1 : 1);
            if (idx < 0 || idx >= t.Steps.Count) throw new ArgumentException($"no such step (have {t.Steps.Count})");
            t.State = "running";
            Enter(idx, "agent");
            return TutorialJson();
        }
    }

    public static JsonObject CompleteStep(JsonNode args)
    {
        lock (Sync)
        {
            var t = Require();
            if (J.Int(args, "expectIndex") is int e && e != t.Index)
                throw new InvalidOperationException($"current step is {t.Index}, not {e}");
            if (t.State == "running" && t.AdvanceAt == null) Complete("agent", immediate: J.Bool(args, "immediate") ?? false);
            return TutorialJson();
        }
    }

    public static JsonObject StopTutorial()
    {
        lock (Sync)
        {
            if (_tut is { Active: true })
            {
                _tut.State = "stopped";
                Events.Emit("tutorial_stopped", new() { ["title"] = _tut.Title, ["index"] = _tut.Index, ["reason"] = "agent" });
            }
            _stepVersion++;
            return TutorialJson();
        }
    }

    /// <summary>Card buttons and hotkeys land here (UI thread).</summary>
    public static void UserAction(string action, string source, string? buttonId = null)
    {
        lock (Sync)
        {
            var t = _tut;
            if (t == null || !t.Active) return;
            switch (action)
            {
                case "done":
                case "next":
                    if (t.State == "running" && t.AdvanceAt == null) Complete(source);
                    break;
                case "back":
                    Events.Emit("step_back", new() { ["from"] = t.Index, ["to"] = Math.Max(0, t.Index - 1), ["source"] = source });
                    t.State = "running";
                    Enter(Math.Max(0, t.Index - 1), source);
                    break;
                case "skip":
                    if (t.State != "running") break;
                    Events.Emit("step_skipped", new() { ["index"] = t.Index, ["id"] = t.Current?.Id, ["source"] = source });
                    Next();
                    break;
                case "undo":
                    Undo(source);
                    break;
                case "stop":
                    t.State = "stopped";
                    Events.Emit("tutorial_stopped", new() { ["title"] = t.Title, ["index"] = t.Index, ["reason"] = source });
                    _stepVersion++;
                    break;
            }
        }
    }

    /// <summary>Clickable on-screen buttons (shape "button") and custom card buttons.</summary>
    public static void ButtonPressed(string markId, string buttonId, string? action, string source)
    {
        lock (Sync)
        {
            var t = _tut;
            var m = Free.FirstOrDefault(x => x.Id == markId) ?? t?.Current?.Targets.FirstOrDefault(x => x.Id == markId);
            Events.Emit("button", new() { ["id"] = buttonId, ["mark"] = markId, ["action"] = action, ["step"] = t?.Active == true ? t.Index : null, ["source"] = source });
            if (m != null && !m.Dismissed && t?.Current?.Targets.Contains(m) == true)
            {
                m.Dismissed = true;
                t.Current.History.Push((m, false));
            }
            switch (action)
            {
                case "dismiss" or "close" or "hide":
                    if (m != null) m.Dismissed = true;
                    break;
                case "done" or "next" or "back" or "skip" or "undo" or "stop":
                    UserAction(action, source);
                    break;
            }
        }
    }

    private static Tutorial Require() => _tut is { Active: true } t ? t : throw new InvalidOperationException("no active tutorial");

    private static void Enter(int idx, string? source = null)
    {
        var t = _tut!;
        t.Index = idx;
        t.AdvanceAt = null;
        t.CompletedHow = null;
        var s = t.Steps[idx];
        s.ResetRuntime();
        _stepVersion++;
        Events.Emit("step_started", new()
        {
            ["index"] = idx, ["total"] = t.Steps.Count, ["id"] = s.Id, ["title"] = s.Title,
            ["targets"] = s.Targets.Count, ["until"] = new JsonArray(s.Until.Select(c => (JsonNode)c.Type).ToArray()), ["source"] = source,
        });
    }

    private static void Complete(string how, bool immediate = false)
    {
        var t = _tut!;
        var s = t.Current!;
        t.CompletedHow = how;
        Events.Emit("step_completed", new() { ["index"] = t.Index, ["id"] = s.Id, ["title"] = s.Title, ["how"] = how });
        int delay = immediate ? 0 : s.AdvanceDelayMs ?? t.AdvanceDelayMs;
        t.AdvanceAt = DateTime.UtcNow.AddMilliseconds(delay);
        _stepVersion++;
    }

    private static void Next()
    {
        var t = _tut!;
        if (t.Index + 1 < t.Steps.Count) Enter(t.Index + 1);
        else Finish();
    }

    private static void Finish()
    {
        var t = _tut!;
        t.AdvanceAt = null;
        if (t.KeepOpen)
        {
            t.State = "waiting";
            Events.Emit("tutorial_waiting", new() { ["title"] = t.Title, ["completed"] = t.Steps.Count });
        }
        else
        {
            t.State = "finished";
            Events.Emit("tutorial_finished", new() { ["title"] = t.Title, ["total"] = t.Steps.Count });
        }
        _stepVersion++;
    }

    private static void Undo(string source)
    {
        var s = _tut?.Current;
        if (s == null || s.History.Count == 0) return;
        var (m, prev) = s.History.Pop();
        m.Dismissed = prev;
        Events.Emit("undo", new() { ["mark"] = m.Id, ["step"] = _tut!.Index, ["dismissed"] = prev, ["source"] = source });
        if (_tut.AdvanceAt != null) { _tut.AdvanceAt = null; _tut.CompletedHow = null; }
        _stepVersion++;
    }

    // =========================================================== clicks (click-consumer thread)

    public static void HandleClick(ClickEvent c)
    {
        var pt = new PointF(c.X, c.Y);
        var (_, rootPid, childPid) = Native.HitWindow(pt);
        if (rootPid == OwnPid || childPid == OwnPid) return;   // card / buttons handle themselves

        lock (Sync)
        {
            var t = _tut;
            var step = t is { State: "running", AdvanceAt: null } ? t.Current : null;
            var candidates = (step?.Targets ?? Enumerable.Empty<Mark>()).Concat(Free);
            Mark? best = null; float bestD = float.MaxValue;
            foreach (var m in candidates)
            {
                var g = m.Geo;
                if (g == null || g.Hits.Count == 0) continue;
                if (m.Button != "any" && m.Button != c.Button) continue;
                if (!m.Anchor.IsScreen && !g.A.Pids.Contains(rootPid) && !g.A.Pids.Contains(childPid)) continue;
                float tol = 5 * g.A.Scale;
                foreach (var h in g.Hits)
                    if (h.Contains(pt, tol))
                    {
                        float d = h.Distance(pt);
                        if (d < bestD) { bestD = d; best = m; }
                    }
            }

            if (best != null)
            {
                OnHit(best, step, pt, c.Button);
                return;
            }
            if (step != null && step.Targets.Any(m => m.Geo != null && (m.Geo.A.Pids.Contains(rootPid) || m.Geo.A.Pids.Contains(childPid))))
            {
                var nearest = step.Targets.Where(m => m.Geo != null && m.Geo.Hits.Count > 0 && !m.Satisfied)
                    .Select(m => (m, d: m.Geo!.Hits.Min(h => h.Distance(pt)))).OrderBy(x => x.d).FirstOrDefault();
                var ev = new JsonObject { ["step"] = t!.Index, ["x"] = c.X, ["y"] = c.Y, ["button"] = c.Button };
                if (nearest.m != null)
                {
                    ev["nearest"] = nearest.m.Id;
                    ev["distance"] = (int)nearest.d;
                    ev["rel"] = J.PtJson(new PointF(pt.X - nearest.m.Geo!.A.P.X, pt.Y - nearest.m.Geo.A.P.Y));
                    if (nearest.m.Geo.A.Bounds is RectangleF ab) ev["relToBounds"] = J.PtJson(new PointF(pt.X - ab.X, pt.Y - ab.Y));
                }
                Events.Emit("miss", ev);
            }
        }
    }

    private static void OnHit(Mark m, Step? step, PointF pt, string button)
    {
        var t = _tut;
        bool inStep = step != null && step.Targets.Contains(m);
        bool toggle = m.ToggleEffective(inStep ? step : null, inStep ? t : null);
        var g = m.Geo!;
        m.Hits++;
        string kind;
        if (m.Check != null) kind = "hit";
        else if (!m.Dismissed)
        {
            kind = "hit";
            if (m.DismissOnClick)
            {
                m.Dismissed = true;
                if (inStep) step!.History.Push((m, false));
            }
        }
        else if (toggle)
        {
            kind = "unhit";
            m.Dismissed = false;
            if (inStep) step!.History.Push((m, true));
        }
        else kind = "hit";

        var h = g.Hits[0];
        Ripples.Add(new Ripple
        {
            C = h.Circle ? h.C : new PointF(h.Rect.X + h.Rect.Width / 2, h.Rect.Y + h.Rect.Height / 2),
            R0 = h.Circle ? h.R : Math.Min(h.Rect.Width, h.Rect.Height) / 2,
            Color = kind == "unhit" ? Palette.Warn : Palette.Success, T0 = DateTime.UtcNow, Scale = g.A.Scale,
        });
        var ev = new JsonObject
        {
            ["mark"] = m.Id, ["name"] = m.Label, ["x"] = (int)pt.X, ["y"] = (int)pt.Y, ["button"] = button,
            ["rel"] = J.PtJson(new PointF(pt.X - g.A.P.X, pt.Y - g.A.P.Y)), ["hits"] = m.Hits,
        };
        if (inStep)
        {
            ev["step"] = t!.Index;
            ev["done"] = step!.Required.Count(x => x.Satisfied);
            ev["required"] = step.Required.Count();
        }
        if (m.Check != null) ev["checkPending"] = true;
        Events.Emit(kind, ev);
    }

    // =========================================================== per-frame (UI thread)

    public sealed class Frame
    {
        public Scene? Scene;
        public bool Changed;
        public List<ButtonGeo> Buttons = new();
        public CardModel Card = new();
    }

    public static Frame Tick()
    {
        var now = DateTime.UtcNow;
        var f = new Frame();
        lock (Sync)
        {
            var t = _tut;
            var step = t is { State: "running" } ? t.Current : null;
            var marks = (step?.Targets ?? Enumerable.Empty<Mark>()).Concat(Free).ToList();

            // what the resolver should keep fresh
            var anchors = marks.Select(m => m.Anchor)
                .Concat(marks.Where(m => m.Check?.Anchor != null).Select(m => m.Check!.Anchor!))
                .Concat(step?.Until.Select(c => c.Anchor ?? c.Pixel?.Anchor).OfType<AnchorSpec>() ?? Enumerable.Empty<AnchorSpec>())
                // the tutorial's main window stays resolved so the card can pick the right monitor
                .Concat(t is { Active: true } && t.Defaults.Process != null ? new[] { t.Defaults } : Array.Empty<AnchorSpec>());
            Resolver.SetWanted(anchors, t is { Active: true } && t.Defaults.Process != null ? new[] { t.Defaults.Process } : Array.Empty<string>());

            int fgPid = ForegroundPid();
            foreach (var m in marks) ComputeMark(m, step != null && step.Targets.Contains(m) ? t : null, fgPid, now);
            var visible = marks.Where(m => m.Geo != null).ToList();
            Declutter(visible);

            if ((now - _lastPixel).TotalMilliseconds >= 150)
            {
                _lastPixel = now;
                foreach (var m in visible.Where(m => m.Check != null)) RunPixelCheck(m);
            }

            // compose the canvas
            var sc = new Scene { Pulse = Animate ? 0.5f + 0.5f * (float)Math.Sin(now.Ticks / (double)TimeSpan.TicksPerMillisecond / 1600.0 * Math.PI * 2) : 1f };
            float dim = 0;
            foreach (var m in visible)
            {
                var g = m.Geo!;
                if (m.ShownAt == null) m.ShownAt = now;
                float alpha = Animate ? (float)Math.Clamp((now - m.ShownAt.Value).TotalMilliseconds / 180.0, 0, 1) : 1f;
                if (!m.Satisfied)
                {
                    foreach (var p in g.Prims) sc.Prims.Add((p, alpha));
                    foreach (var b in g.Buttons) f.Buttons.Add(b);
                    foreach (var h in g.Hits) sc.Holes.Add(h);
                    dim = Math.Max(dim, g.Dim);
                    if (step != null && step.Targets.Contains(m)) dim = Math.Max(dim, step.Dim ?? t!.Dim ?? 0);
                    sc.HoleScale = g.A.Scale;
                }
                else
                {
                    foreach (var p in g.DonePrims) sc.Prims.Add((p, 1f));
                }
                f.Card.Avoid.Add(g.Bounds);
            }
            foreach (var m in marks.Where(m => m.Geo == null)) m.ShownAt = null;
            sc.Dim = dim;
            if (dim > 0) sc.Pulse = 1f;    // keep full-screen frames static
            Ripples.RemoveAll(r => (now - r.T0).TotalMilliseconds > Ripple.DurationMs);
            sc.Ripples.AddRange(Ripples);
            f.Scene = sc;

            if (t is { Active: true }) EvaluateTutorial(t, now, fgPid);
            BuildCard(f.Card, _tut, fgPid);

            var sig = Signature(sc);
            f.Changed = sig != _lastSignature || sc.Ripples.Count > 0;
            _lastSignature = sig;
        }
        return f;
    }

    private static string Signature(Scene sc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append((int)(sc.Dim * 100)).Append('|').Append((int)(sc.Pulse * 24)).Append('|');
        foreach (var (p, a) in sc.Prims)
            sb.Append((int)p.Kind).Append(',').Append((int)p.A.X).Append(',').Append((int)p.A.Y).Append(',').Append((int)p.B.X).Append(',').Append((int)p.B.Y)
              .Append(',').Append((int)p.R).Append(',').Append((int)p.Rect.X).Append(',').Append((int)p.Rect.Y).Append(',').Append((int)p.Rect.Width)
              .Append(',').Append((int)p.Rect.Height).Append(',').Append((int)(a * 20)).Append(p.On ? '1' : '0').Append(p.Text).Append(';');
        foreach (var h in sc.Holes) sb.Append((int)h.C.X).Append(',').Append((int)h.Rect.X).Append(';');
        return sb.ToString();
    }

    private static int ForegroundPid()
    {
        var fg = GetForegroundWindow();
        return fg == IntPtr.Zero ? 0 : Native.ProcessId(fg);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static string ProcName(int pid)
    {
        lock (ProcNames)
        {
            if (ProcNames.TryGetValue(pid, out var n)) return n;
            try { using var p = Process.GetProcessById(pid); n = p.ProcessName; } catch { n = ""; }
            if (ProcNames.Count > 512) ProcNames.Clear();
            return ProcNames[pid] = n;
        }
    }

    /// <summary>Is the user currently working in this app? (foreground window belongs to it)</summary>
    private static bool AppActive(int[] pids, string? process, int fgPid)
    {
        if (fgPid == 0) return false;
        if (pids.Contains(fgPid)) return true;
        return process != null && ProcName(fgPid).Equals(process, StringComparison.OrdinalIgnoreCase);
    }

    public static AnchorGeo? AnchorNow(AnchorSpec a, out string reason)
    {
        reason = "ok";
        if (a.IsScreen)
        {
            var p = new PointF(a.Dx, a.Dy);
            return new AnchorGeo { P = p, Scale = Native.ScaleAt(p) };
        }
        var r = Resolver.Get(a.Key);
        if (r == null) { reason = "resolving"; return null; }
        if (!r.Found) { reason = r.Reason; return null; }
        var root = r.Root;
        if (!Native.IsWindow(root)) { reason = "closed"; return null; }
        if (Native.IsIconic(root)) { reason = "minimized"; return null; }
        if (!Native.IsWindowVisible(root) || Native.IsCloaked(root)) { reason = "hidden"; return null; }
        var frame = Native.FrameRect(root);
        RectangleF bounds = r.Rel is RectangleF rel
            ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height)
            : a.Client ? Native.ClientScreenRect(root) : frame;
        var o = a.OriginOf(bounds);
        return new AnchorGeo
        {
            Bounds = bounds, P = new PointF(o.X + a.Dx, o.Y + a.Dy), Root = root, Pids = r.Pids,
            Scale = Native.ScaleAt(new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)),
        };
    }

    private static void ComputeMark(Mark m, Tutorial? t, int fgPid, DateTime now)
    {
        var ag = AnchorNow(m.Anchor, out var reason);
        if (ag == null) { m.Geo = null; m.Visibility = reason; return; }
        if (!m.Anchor.IsScreen)
        {
            bool hideInactive = m.HideInactive ?? t?.HideWhenInactive ?? HideWhenInactiveDefault;
            if (hideInactive && !AppActive(ag.Pids, m.Anchor.Process, fgPid)) { m.Geo = null; m.Visibility = "inactive"; return; }
        }
        var geo = Layout.Build(m, ag, m.Satisfied);
        if (!m.Anchor.IsScreen)
        {
            if (!OnAnyMonitor(geo.Center)) { m.Geo = null; m.Visibility = "offscreen"; return; }
            if (!ShowsAnchorWindow(geo.Center, ag)) { m.Geo = null; m.Visibility = "occluded"; return; }
        }
        m.Geo = geo;
        m.Visibility = "visible";
    }

    private static bool OnAnyMonitor(PointF p) => Native.VirtualScreen().Contains((int)p.X, (int)p.Y)
        && Native.MonitorRectAt(p).Contains((int)p.X, (int)p.Y);

    /// <summary>
    /// Is the anchor's own window what is actually on screen at <paramref name="p"/>? Another app's window
    /// on top (or one of the app's own floating windows / menus covering a mark that belongs elsewhere)
    /// hides the mark instead of making it float over unrelated UI.
    /// </summary>
    private static bool ShowsAnchorWindow(PointF p, AnchorGeo ag)
    {
        var (root, rootPid, childPid) = Native.HitWindow(p);
        if (root == IntPtr.Zero) return false;
        if (rootPid == OwnPid) return true;
        if (root == ag.Root) return true;
        if (ag.Pids.Contains(rootPid) || ag.Pids.Contains(childPid))
        {
            var cls = Native.ClassName(root);
            if (cls.Contains("tooltip", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void RunPixelCheck(Mark m)
    {
        var chk = m.Check!;
        AnchorGeo? ag = chk.Anchor == null ? m.Geo!.A : AnchorNow(chk.Anchor, out _);
        if (ag == null) return;
        var pt = chk.At is PointF at ? new PointF(ag.P.X + at.X, ag.P.Y + at.Y) : m.Geo!.Center;
        if (!ShowsAnchorWindow(pt, ag)) return;          // covered: don't conclude anything
        var color = ScreenCapture.Sample(pt, chk.Radius);
        bool ok = chk.Evaluate(color);
        if (m.CheckOk != ok)
        {
            bool first = m.CheckOk == null;
            m.CheckOk = ok;
            Events.Emit("check_changed", new()
            {
                ["mark"] = m.Id, ["ok"] = ok, ["initial"] = first, ["color"] = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
                ["at"] = J.PtJson(pt), ["step"] = _tut?.Index,
            });
        }
    }

    /// <summary>
    /// Keeps marks from piling on each other: auto-sized rings of neighbouring targets shrink to fit,
    /// and auto-placed labels / info boxes / buttons hop to the first free spot around their target.
    /// </summary>
    private static void Declutter(List<Mark> visible)
    {
        // 1) neighbouring auto-sized rings
        var rings = new List<(Prim p, float s)>();
        foreach (var m in visible)
            for (int i = 0; i < m.Geo!.Prims.Count; i++)
            {
                var p = m.Geo.Prims[i];
                if (p.Kind == PrimKind.Circle && p.AutoSize) rings.Add((p, m.Geo.A.Scale));
            }
        for (int i = 0; i < rings.Count; i++)
            for (int k = i + 1; k < rings.Count; k++)
            {
                var (a, s) = rings[i]; var b = rings[k].p;
                float d = (float)Math.Sqrt((a.A.X - b.A.X) * (a.A.X - b.A.X) + (a.A.Y - b.A.Y) * (a.A.Y - b.A.Y));
                float gap = 4 * s + a.Width;
                if (d > 1 && a.R + b.R + gap > d)
                {
                    float r = Math.Max(9 * s, (d - gap) / 2);
                    a.R = Math.Min(a.R, r); b.R = Math.Min(b.R, r);
                }
            }
        foreach (var m in visible)
            foreach (var h in m.Geo!.Hits.Where(h => h.Circle))
            {
                var ring = m.Geo.Prims.FirstOrDefault(p => p.Kind == PrimKind.Circle && p.A == h.C);
                if (ring != null) h.R = ring.R;
            }

        // 2) movable boxes
        var obstacles = new List<RectangleF>();
        var segments = new List<(PointF a, PointF b, float w)>();
        foreach (var m in visible)
        {
            if (m.Satisfied) continue;
            foreach (var p in m.Geo!.Prims)
            {
                if (p.Kind == PrimKind.Arrow) segments.Add((p.A, p.B, p.Width * 2));
                else if (!(p.Auto && (p.Kind == PrimKind.Label || p.Kind == PrimKind.Info))) obstacles.Add(p.Extent());
            }
            foreach (var b in m.Geo.Buttons.Where(b => !b.Auto)) obstacles.Add(b.Rect);
        }
        foreach (var m in visible)
        {
            if (m.Satisfied) continue;
            var g = m.Geo!;
            foreach (var p in g.Prims.Where(p => p.Auto && (p.Kind == PrimKind.Label || p.Kind == PrimKind.Info)))
            {
                p.Rect = Place(p.Rect, g.TargetBounds, obstacles, segments, g.A.Scale);
                obstacles.Add(p.Rect);
            }
            foreach (var b in g.Buttons.Where(b => b.Auto))
            {
                b.Rect = Rectangle.Round(Place(b.Rect, g.TargetBounds, obstacles, segments, g.A.Scale));
                obstacles.Add(b.Rect);
            }
            RectangleF all = g.Bounds;
            foreach (var p in g.Prims) all = RectangleF.Union(all, p.Extent());
            foreach (var b in g.Buttons) all = RectangleF.Union(all, b.Rect);
            g.Bounds = all;
        }
    }

    private static RectangleF Place(RectangleF box, RectangleF target, List<RectangleF> obstacles, List<(PointF a, PointF b, float w)> segments, float s)
    {
        bool Free(RectangleF r, Rectangle mon) =>
            mon.Contains(Rectangle.Round(r)) && !obstacles.Any(o => o.Width > 0 && o.IntersectsWith(Inflate(r, 3 * s)))
            && !segments.Any(sg => SegmentHits(sg.a, sg.b, Inflate(r, sg.w)));
        var mon = Native.WorkAreaAt(new PointF(target.X + target.Width / 2, target.Y + target.Height / 2));
        if (Free(box, mon)) return box;
        float w = box.Width, h = box.Height, cx = target.X + target.Width / 2 - w / 2, cy = target.Y + target.Height / 2 - h / 2;
        for (int ring = 0; ring < 6; ring++)
        {
            float gap = (12 + ring * 26) * s;
            var cands = new[]
            {
                new PointF(cx, target.Bottom + gap), new PointF(cx, target.Top - gap - h),
                new PointF(target.Right + gap, cy), new PointF(target.Left - gap - w, cy),
                new PointF(target.Right + gap, target.Bottom + gap), new PointF(target.Left - gap - w, target.Bottom + gap),
                new PointF(target.Right + gap, target.Top - gap - h), new PointF(target.Left - gap - w, target.Top - gap - h),
            };
            foreach (var c in cands)
            {
                var r = new RectangleF(c, box.Size);
                if (Free(r, mon)) return r;
            }
        }
        return box;
    }

    private static RectangleF Inflate(RectangleF r, float by) { r.Inflate(by, by); return r; }

    /// <summary>Does segment a-b cross rectangle r? (sampled; boxes are much larger than the step)</summary>
    private static bool SegmentHits(PointF a, PointF b, RectangleF r)
    {
        float len = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        int n = Math.Max(2, (int)(len / 4));
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            if (r.Contains(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)) return true;
        }
        return false;
    }

    private static void EvaluateTutorial(Tutorial t, DateTime now, int fgPid)
    {
        if (t.State != "running") return;
        var s = t.Current;
        if (s == null) return;
        if (t.AdvanceAt is DateTime at)
        {
            if (now >= at) Next();
            return;
        }
        // frozen while the user is elsewhere: a minimised / backgrounded app must not look "closed"
        if (t.Defaults.Process != null && !AppActive(Array.Empty<int>(), t.Defaults.Process, fgPid)) return;
        if ((now - _lastCond).TotalMilliseconds < 100) return;
        _lastCond = now;

        foreach (var c in s.Until)
        {
            switch (c.Type)
            {
                case "all_clicked":
                    var req = s.Required.ToList();
                    c.Met = req.Count > 0 && req.All(m => m.Satisfied);
                    break;
                case "count":
                    c.Met = s.Targets.Sum(m => m.Hits) >= Math.Max(1, c.Count);
                    break;
                case "appear":
                case "disappear":
                {
                    var r = Resolver.Get(c.Anchor!.Key);
                    if (r == null || r.Reason == "searching") break;        // no verdict yet
                    bool present = r.Found && Native.IsWindow(r.Root) && !Native.IsIconic(r.Root);
                    if (present) c.SeenPresent = true;
                    c.Met = c.Type == "appear" ? present : c.SeenPresent && !present;
                    break;
                }
                case "pixel":
                {
                    if ((now - _lastPixel).TotalMilliseconds > 20) break;  // piggy-back on the pixel cadence
                    var ag = AnchorNow(c.Pixel!.Anchor!, out _);
                    if (ag == null) break;
                    var pt = c.Pixel.At is PointF pa ? new PointF(ag.P.X + pa.X, ag.P.Y + pa.Y) : ag.P;
                    if (!ShowsAnchorWindow(pt, ag)) break;
                    c.Met = c.Pixel.Evaluate(ScreenCapture.Sample(pt, c.Pixel.Radius));
                    break;
                }
            }
        }
        var active = s.Until.Where(c => c.Type != "manual").ToList();
        if (active.Count == 0) return;
        var met = active.FirstOrDefault(c => c.Met);
        bool done = s.UntilAll ? active.All(c => c.Met) : met != null;
        if (done) Complete(s.UntilAll ? "all" : met!.Type switch { "all_clicked" => "targets", var x => x });
    }

    private static void BuildCard(CardModel card, Tutorial? t, int fgPid)
    {
        card.StepVersion = _stepVersion;
        if (t == null || !t.Active) return;
        var s = t.Current;
        // the card follows the same rule as the marks: only while the user is in the app
        if (t.HideWhenInactive && t.Defaults.Process != null && !AppActive(Array.Empty<int>(), t.Defaults.Process, fgPid)) return;
        var appGeo = t.Defaults.Process != null ? AnchorNow(t.Defaults, out _) : null;
        card.Visible = true;
        card.State = t.State;
        card.Index = t.Index;
        card.Total = t.Steps.Count;
        card.AppFrame = appGeo?.Bounds;
        card.Completed = t.AdvanceAt != null;
        if (t.State == "waiting" || s == null)
        {
            card.Title = t.Title.Length > 0 ? t.Title : "Туториал";
            card.Text = "Готовлю следующий шаг…";
            card.Controls = new HashSet<string> { "stop" };
        }
        else
        {
            card.Title = s.Title;
            card.Text = s.Text;
            var req = s.Required.ToList();
            card.Required = req.Count;
            card.Done = req.Count(m => m.Satisfied);
            card.CanBack = t.Index > 0;
            card.CanUndo = s.History.Count > 0;
            if (s.Checklist ?? req.Count(m => m.Label != null) >= 2)
                for (int i = 0; i < req.Count; i++) card.Checklist.Add((req[i].Label ?? $"Цель {i + 1}", req[i].Satisfied));
            card.Extra = s.Buttons;
            card.Controls = s.Controls ?? new HashSet<string> { "back", "undo", "skip", "done", "stop" };
            var firstVisible = s.Targets.FirstOrDefault(m => m.Geo != null);
            if (firstVisible != null) card.Focus = firstVisible.Geo!.Center;
        }
        if (card.Focus == default && appGeo?.Bounds is RectangleF ab) card.Focus = new PointF(ab.X + ab.Width / 2, ab.Y + ab.Height / 2);
    }

    // =========================================================== status

    public static JsonObject Status()
    {
        lock (Sync)
        {
            var o = new JsonObject
            {
                ["tutorial"] = TutorialJson(),
                ["marks"] = new JsonArray(Free.Select(m => (JsonNode)MarkJson(m)).ToArray()),
                ["virtualScreen"] = J.RectJson(Native.VirtualScreen()),
                ["foreground"] = ForegroundInfo(),
                ["lastEventSeq"] = Events.LastSeq,
            };
            return o;
        }
    }

    private static JsonObject ForegroundInfo()
    {
        var fg = GetForegroundWindow();
        int pid = fg == IntPtr.Zero ? 0 : Native.ProcessId(fg);
        return new JsonObject { ["hwnd"] = $"0x{fg.ToInt64():X}", ["title"] = Native.WindowTitle(fg), ["process"] = pid == 0 ? null : ProcName(pid) };
    }

    private static JsonObject TutorialJson()
    {
        var t = _tut;
        if (t == null) return new JsonObject { ["state"] = "none" };
        var o = new JsonObject
        {
            ["state"] = t.State, ["title"] = t.Title, ["index"] = t.Index, ["total"] = t.Steps.Count,
            ["app"] = t.Defaults.Process, ["completing"] = t.AdvanceAt != null,
        };
        var s = t.Current;
        if (s != null && t.Active)
            o["step"] = new JsonObject
            {
                ["id"] = s.Id, ["title"] = s.Title,
                ["targets"] = new JsonArray(s.Targets.Select(m => (JsonNode)MarkJson(m)).ToArray()),
                ["until"] = new JsonArray(s.Until.Select(c => (JsonNode)c.ToJson()).ToArray()),
                ["undoDepth"] = s.History.Count,
            };
        return o;
    }

    private static JsonObject MarkJson(Mark m)
    {
        var o = new JsonObject
        {
            ["id"] = m.Id, ["name"] = m.Label, ["anchor"] = m.Anchor.Describe(), ["visibility"] = m.Visibility,
            ["dismissed"] = m.Dismissed, ["satisfied"] = m.Satisfied, ["hits"] = m.Hits,
        };
        if (m.Check != null) o["check"] = new JsonObject { ["expr"] = m.Check.Expr, ["ok"] = m.CheckOk };
        if (m.Geo != null)
        {
            o["point"] = J.PtJson(m.Geo.A.P);
            o["center"] = J.PtJson(m.Geo.Center);
            if (m.Geo.A.Bounds is RectangleF b) o["anchorBounds"] = J.RectJson(b);
            o["bounds"] = J.RectJson(m.Geo.Bounds);
        }
        return o;
    }
}
