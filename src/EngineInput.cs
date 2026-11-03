using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace TutorialOverlay;

/// <summary>Input-driven features: point picking (calibration), drag &amp; drop, key chords, UIA state conditions.</summary>
internal static partial class Engine
{
    // =========================================================== input dispatch (input-consumer thread)

    private static (PointF pt, string button)? _down;

    public static void HandleInput(InputEvent e)
    {
        switch (e.Kind)
        {
            case InputKind.MouseDown:
                if (_pick != null && TryPick(e)) return;
                _down = (new PointF(e.X, e.Y), e.Button);
                if (!e.Swallowed) { MotionDown(new PointF(e.X, e.Y), e.Button); HandleClick(e); }
                break;
            case InputKind.MouseUp:
                if (_down is { } d && d.button == e.Button)
                {
                    var end = new PointF(e.X, e.Y);
                    float dist = (float)Math.Sqrt((end.X - d.pt.X) * (end.X - d.pt.X) + (end.Y - d.pt.Y) * (end.Y - d.pt.Y));
                    if (dist >= 10 * Native.ScaleAt(end)) HandleDrag(d.pt, end, e.Button);
                    MotionUp(end, e.Button);
                }
                _down = null;
                break;
            case InputKind.Move:
                MotionMove(new PointF(e.X, e.Y));
                break;
            case InputKind.Wheel:
                MotionWheel(e);
                break;
            case InputKind.KeyDown:
                HandleKey(e);
                break;
        }
    }

    // =========================================================== pick: "click the spot" calibration

    private sealed class PickSession
    {
        public readonly TaskCompletionSource<JsonObject> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AnchorSpec? Anchor;
        public int Count = 1;
        public readonly JsonArray Points = new();
        public readonly List<PointF> Rel = new();
        public bool PassThrough;
        public string? Prompt;
    }

    private static volatile PickSession? _pick;

    /// <summary>Waits for the user to click 1..N points; the clicks are swallowed unless passThrough.</summary>
    public static async Task<JsonObject> Pick(JsonNode args, CancellationToken ct)
    {
        var s = new PickSession
        {
            Anchor = J.Has(args, "anchor") ? AnchorSpec.Parse(J.Get(args, "anchor"), _tut?.Defaults) : null,
            Count = Math.Clamp(J.Int(args, "count") ?? 1, 1, 20),
            PassThrough = J.Bool(args, "passThrough") ?? false,
            Prompt = J.Str(args, "prompt") ?? J.Str(args, "text"),
        };
        int timeout = Math.Clamp(J.Int(args, "timeoutMs") ?? 60000, 1000, 600000);
        lock (Sync)
        {
            _pick?.Tcs.TrySetResult(new JsonObject { ["cancelled"] = "superseded" });
            _pick = s;
            InputHook.SwallowNextClick = !s.PassThrough;
        }
        Events.Emit("pick_started", new() { ["count"] = s.Count, ["anchor"] = s.Anchor?.Describe() });
        using var reg = ct.Register(() => EndPick(s, "client gone"));
        var finished = await Task.WhenAny(s.Tcs.Task, Task.Delay(timeout, CancellationToken.None));
        if (finished != s.Tcs.Task) EndPick(s, "timeout");
        return await s.Tcs.Task;
    }

    private static void EndPick(PickSession s, string? cancelled)
    {
        lock (Sync)
        {
            if (_pick != s) return;
            _pick = null;
            InputHook.SwallowNextClick = false;
        }
        var res = new JsonObject { ["points"] = s.Points.DeepClone() };
        if (cancelled != null) res["cancelled"] = cancelled;
        if (s.Rel.Count >= 2)
        {
            // two clicks on neighbouring cells give a grid step directly
            var a = s.Rel[0]; var b = s.Rel[^1];
            res["gridHint"] = new JsonObject
            {
                ["from"] = J.PtJson(a), ["step"] = J.PtJson(new PointF((b.X - a.X) / Math.Max(1, s.Rel.Count - 1), (b.Y - a.Y) / Math.Max(1, s.Rel.Count - 1))),
                ["note"] = "if the clicks were consecutive cells of one row; use as grid {from, step}",
            };
        }
        Events.Emit("pick_finished", new() { ["points"] = s.Points.Count, ["cancelled"] = cancelled });
        s.Tcs.TrySetResult(res);
    }

    private static bool TryPick(InputEvent e)
    {
        var s = _pick;
        if (s == null) return false;
        var pt = new PointF(e.X, e.Y);
        var (_, rootPid, childPid) = Native.HitWindow(pt);
        if (rootPid == OwnPid || childPid == OwnPid)
        {
            InputHook.SwallowNextClick = !s.PassThrough;   // a click on our own card doesn't count
            return true;
        }
        JsonObject info;
        try { info = PointInfo(pt, s.Anchor, out var rel); if (rel is PointF r) s.Rel.Add(r); }
        catch (Exception ex) { info = new JsonObject { ["x"] = e.X, ["y"] = e.Y, ["error"] = ex.Message }; }
        info["button"] = e.Button;
        s.Points.Add(info);
        Events.Emit("pick", new() { ["index"] = s.Points.Count, ["x"] = e.X, ["y"] = e.Y, ["rel"] = info["rel"]?.DeepClone() });
        if (s.Points.Count >= s.Count) EndPick(s, null);
        else InputHook.SwallowNextClick = !s.PassThrough;
        return !s.PassThrough;   // a passed-through click still acts as a normal click
    }

    /// <summary>Everything an agent needs to anchor something at this point.</summary>
    private static JsonObject PointInfo(PointF pt, AnchorSpec? anchor, out PointF? rel)
    {
        rel = null;
        var (root, rootPid, _) = Native.HitWindow(pt);
        var frame = Native.FrameRect(root);
        var color = ScreenCapture.Sample(pt, 1);
        var o = new JsonObject
        {
            ["x"] = (int)pt.X, ["y"] = (int)pt.Y,
            ["process"] = ProcName(rootPid),
            ["window"] = new JsonObject { ["hwnd"] = $"0x{root.ToInt64():X}", ["title"] = Native.WindowTitle(root), ["class"] = Native.ClassName(root), ["frame"] = J.RectJson(frame) },
            ["relToWindow"] = J.PtJson(new PointF(pt.X - frame.X, pt.Y - frame.Y)),
            ["color"] = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            ["rgb"] = new JsonArray(color.R, color.G, color.B),
        };
        if (anchor != null)
        {
            AnchorGeo? ag;
            lock (Sync) ag = AnchorNow(anchor, out _);
            if (ag == null)
            {
                var r = Resolver.ResolveNow(anchor);
                if (r.Found)
                {
                    var f = Native.FrameRect(r.Root);
                    RectangleF b = r.Rel is RectangleF rr ? new RectangleF(f.X + rr.X, f.Y + rr.Y, rr.Width, rr.Height) : f;
                    var origin = anchor.OriginOf(b);
                    ag = new AnchorGeo { Bounds = b, P = new PointF(origin.X + anchor.Dx, origin.Y + anchor.Dy) };
                }
            }
            if (ag != null)
            {
                rel = new PointF(pt.X - ag.P.X, pt.Y - ag.P.Y);
                o["rel"] = J.PtJson(rel.Value);
                if (ag.Bounds is RectangleF ab) { o["anchorBounds"] = J.RectJson(ab); o["relToBounds"] = J.PtJson(new PointF(pt.X - ab.X, pt.Y - ab.Y)); }
            }
            else o["anchorError"] = "anchor not found";
        }

        // the UIA element under the point, with a few ancestors for context
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            var chain = new JsonArray();
            var walker = TreeWalker.ControlViewWalker;
            AutomationElement? cur = el;
            for (int i = 0; i < 4 && cur != null; i++)
            {
                var info = Resolver.Read(cur, cached: false);
                if (info == null) break;
                var node = new JsonObject { ["role"] = info.Role, ["name"] = info.Name };
                if (info.AutomationId.Length > 0) node["automationId"] = info.AutomationId;
                if (info.ClassName.Length > 0) node["className"] = info.ClassName;
                if (info.Rect is RectangleF er)
                {
                    node["rect"] = J.RectJson(er);
                    node["offsetInElement"] = J.PtJson(new PointF(pt.X - er.X, pt.Y - er.Y));
                }
                chain.Add(node);
                try { cur = walker.GetParent(cur); } catch (Exception ex) when (Resolver.IsGone(ex)) { cur = null; }
                if (cur != null && cur == AutomationElement.RootElement) break;
            }
            o["element"] = chain.Count > 0 ? chain[0]!.DeepClone() : null;
            o["ancestors"] = chain;
            // a ready-made anchor: nearest named ancestor + offset
            var named = chain.OfType<JsonObject>().FirstOrDefault(n => (J.Str(n, "name") ?? "").Length > 0 && J.Get(n, "rect") != null);
            if (named != null)
                o["suggestedAnchor"] = new JsonObject
                {
                    ["process"] = ProcName(rootPid),
                    ["element"] = new JsonObject { ["name"] = J.Str(named, "name"), ["role"] = J.Str(named, "role") },
                    ["offset"] = named["offsetInElement"]!.DeepClone(),
                };
        }
        catch (Exception ex) when (Resolver.IsGone(ex) || ex is InvalidOperationException) { }
        return o;
    }

    private static void AddPickVisuals(Scene sc, List<RectangleF> avoid)
    {
        var s = _pick;
        if (s == null) return;
        Native.GetCursorPos(out var cp);
        var p = new PointF(cp.X, cp.Y);
        float k = Native.ScaleAt(p);
        var t = Settings.Current.Text;
        sc.Prims.Add((new Prim { Kind = PrimKind.Circle, A = p, R = 14 * k, Color = Palette.Accent, Width = 2.5f * k, Scale = k }, 1f));
        string title = s.Count > 1 ? string.Format(t.PickCount, s.Points.Count + 1, s.Count) : t.PickTitle;
        string body = s.Prompt ?? t.PickText;
        float px = 14 * k, maxW = 300 * k;
        var bs = Theme.Measure(body, Theme.Font(px), maxW);
        var ts = Theme.Measure(title, Theme.Font(px * 1.05f, true), maxW);
        float w = Math.Max(bs.Width, ts.Width) + 24 * k + 30 * k, h = Math.Max(40 * k, bs.Height + ts.Height + 19 * k);
        var mon = Native.MonitorRectAt(p);
        float x = p.X + 26 * k, y = p.Y + 26 * k;
        if (x + w > mon.Right) x = p.X - 26 * k - w;
        if (y + h > mon.Bottom) y = p.Y - 26 * k - h;
        var box = new RectangleF(x, y, w, h);
        sc.Prims.Add((new Prim { Kind = PrimKind.Info, Rect = box, Title = title, Text = body, Size = px, Color = Palette.Accent, Scale = k, Radius = 8 * k }, 1f));
        avoid.Add(box);
    }

    // =========================================================== drag & drop

    private static (bool circle, PointF c, float r, RectangleF rect, AnchorGeo ag)? RegionGeo(ScreenRegion reg)
    {
        var ag = AnchorNow(reg.Anchor, out _);
        if (ag == null) return null;
        if (reg.Rect is RectangleF rr)
        {
            var r = new RectangleF(ag.P.X + rr.X, ag.P.Y + rr.Y, rr.Width, rr.Height);
            return (false, new PointF(r.X + r.Width / 2, r.Y + r.Height / 2), 0, r, ag);
        }
        if (reg.Radius is float rad || ag.Bounds == null) return (true, ag.P, (reg.Radius ?? 24) * (reg.Radius == null ? ag.Scale : 1), RectangleF.Empty, ag);
        var b = ag.Bounds.Value;
        return (false, new PointF(b.X + b.Width / 2, b.Y + b.Height / 2), 0, b, ag);
    }

    private static bool InRegion(ScreenRegion reg, PointF p)
    {
        var g = RegionGeo(reg);
        if (g == null) return false;
        var v = g.Value;
        if (v.circle) return (p.X - v.c.X) * (p.X - v.c.X) + (p.Y - v.c.Y) * (p.Y - v.c.Y) <= v.r * v.r;
        var r = v.rect; r.Inflate(4 * v.ag.Scale, 4 * v.ag.Scale);
        return r.Contains(p);
    }

    private static void HandleDrag(PointF from, PointF to, string button)
    {
        lock (Sync)
        {
            var t = _tut;
            var step = t is { State: TutorialState.Running, AdvanceAt: null } ? t.Current : null;
            var drags = step?.Until.Where(c => c.Type == "drag").ToList();
            if (step == null || drags == null || drags.Count == 0) return;
            bool any = false;
            foreach (var c in drags)
            {
                if (c.Met || (c.Button != "any" && c.Button != button)) continue;
                if (InRegion(c.From!, from) && InRegion(c.To!, to))
                {
                    if (!DragHoldOk(c))
                    {
                        Events.Emit("drag", new() { ["step"] = t!.Index, ["matched"] = false, ["reason"] = "released_key", ["hold"] = string.Join("+", c.HoldNames) });
                        continue;
                    }
                    c.Met = true;
                    any = true;
                    Events.Emit("drag", new() { ["step"] = t!.Index, ["matched"] = true, ["from"] = J.PtJson(from), ["to"] = J.PtJson(to), ["button"] = button });
                }
            }
            if (!any)
            {
                // only report drags inside the tutorial's app (never other apps' activity)
                var (_, rootPid, _) = Native.HitWindow(from);
                if (t!.Defaults.Process != null && ProcName(rootPid).Equals(t.Defaults.Process, StringComparison.OrdinalIgnoreCase))
                    Events.Emit("drag", new()
                    {
                        ["step"] = t.Index, ["matched"] = false, ["from"] = J.PtJson(from), ["to"] = J.PtJson(to), ["button"] = button,
                        ["startedInFrom"] = drags.Any(c => InRegion(c.From!, from)), ["endedInTo"] = drags.Any(c => InRegion(c.To!, to)),
                    });
            }
        }
    }

    /// <summary>Shows drag conditions: a ring/frame on the source, a frame on the drop zone and an arrow between.</summary>
    private static void AddDragVisuals(Step step, Scene sc, List<RectangleF> avoid, int fgPid)
    {
        foreach (var c in step.Until.Where(c => c.Type == "drag" && c.Show && !c.Met))
        {
            var f = RegionGeo(c.From!);
            var to = RegionGeo(c.To!);
            if (f == null || to == null) continue;
            var fv = f.Value; var tv = to.Value;
            bool hideInactive = _tut?.HideWhenInactive ?? HideWhenInactiveDefault;
            if (hideInactive && !c.From!.Anchor.IsScreen && !AppActive(fv.ag.Pids, c.From.Anchor.Process, fgPid)) continue;
            if (!c.From!.Anchor.IsScreen && !ShowsAnchorWindow(fv.c, fv.ag)) continue;
            float s = fv.ag.Scale;
            var blue = Palette.Named["blue"];
            Prim RegionPrim((bool circle, PointF c, float r, RectangleF rect, AnchorGeo ag) g, Color col) => g.circle
                ? new Prim { Kind = PrimKind.Circle, A = g.c, R = g.r, Color = col, Width = 3 * s, Scale = s }
                : new Prim { Kind = PrimKind.Rect, Rect = g.rect, Color = col, Width = 3 * s, Radius = 8 * s, Scale = s };
            var fp = RegionPrim(fv, blue);
            var tp = RegionPrim(tv, Palette.Success);
            float dx = tv.c.X - fv.c.X, dy = tv.c.Y - fv.c.Y, len = Math.Max(1, (float)Math.Sqrt(dx * dx + dy * dy));
            float ux = dx / len, uy = dy / len;
            float startOff = fv.circle ? fv.r + 6 * s : 0, endOff = tv.circle ? tv.r + 6 * s : Math.Min(len / 3, Math.Min(tv.rect.Width, tv.rect.Height) / 2);
            var arrow = new Prim
            {
                Kind = PrimKind.Arrow, A = new PointF(fv.c.X + ux * startOff, fv.c.Y + uy * startOff),
                B = new PointF(tv.c.X - ux * endOff, tv.c.Y - uy * endOff), Color = blue, Width = 4 * s, Scale = s,
            };
            foreach (var p in new[] { fp, tp, arrow }) { sc.Prims.Add((p, 1f)); avoid.Add(p.Extent()); }
        }
    }

    // =========================================================== keys

    private static void HandleKey(InputEvent e)
    {
        if (e.Vk == 0x1B && _pick is PickSession ps) { EndPick(ps, "esc"); return; }
        lock (Sync)
        {
            var t = _tut;
            var step = t is { State: TutorialState.Running, AdvanceAt: null } ? t.Current : null;
            if (step == null) return;
            int fgPid = ForegroundPid();
            foreach (var c in step.Until.Where(c => c.Type == "key" && !c.Met))
            {
                var proc = c.Anchor?.Process ?? t!.Defaults.Process;
                if (proc != null && !ProcName(fgPid).Equals(proc, StringComparison.OrdinalIgnoreCase)) continue;
                var hit = c.Keys.FirstOrDefault(k => k.Vk == e.Vk && k.Mods == e.Mods);
                if (hit.Vk == 0) continue;
                c.Met = true;
                // only the expected chord is ever reported — other keystrokes are not recorded anywhere
                Events.Emit("key_matched", new() { ["step"] = t!.Index, ["keys"] = hit.ToString() });
            }
        }
    }

    private static bool WantsKeys(Step? step) => _pick != null || (step != null && step.Until.Any(c => c.Type == "key" && !c.Met));

    // =========================================================== UIA state conditions

    private static void EvalUia(Condition c)
    {
        var r = Resolver.Get(c.Anchor!.Key);
        if (r == null || r.Reason == "searching") return;
        string? v = c.Property == "exists" ? (r.Found ? "true" : "false") : r.Found ? r.Value : null;
        bool ok;
        if (c.EqualsText != null) ok = v != null && v.Equals(c.EqualsText, StringComparison.OrdinalIgnoreCase);
        else if (c.ContainsText != null) ok = v != null && v.Contains(c.ContainsText, StringComparison.OrdinalIgnoreCase);
        else if (c.Matches != null)
        {
            try { ok = v != null && Regex.IsMatch(v, c.Matches, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
            catch (Exception) { ok = false; }
        }
        else ok = v != null && v.Length > 0 && v.ToLowerInvariant() is not ("false" or "off" or "0" or "collapsed" or "indeterminate");
        c.Met = ok ^ c.Not;
    }

    private static IEnumerable<AnchorSpec> InputAnchors(Step? step)
    {
        if (_pick?.Anchor is AnchorSpec pa) yield return pa;
        if (step == null) yield break;
        foreach (var c in step.Until)
        {
            if (c.From != null) yield return c.From.Anchor;
            if (c.To != null) yield return c.To.Anchor;
            // a scroll condition's area: without it the area never resolved — no wheel hint, and every wheel turn
            // counted as "outside the area"
            if (c.Area != null) yield return c.Area.Anchor;
        }
    }
}
