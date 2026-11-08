using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace TutorialOverlay.Tests;

/// <summary>
/// The robot user: plays tutorials in real apps through the running server, the way a person would — it reads where
/// the current step's target is (status), clicks it, presses the step's keys or scrolls, and waits for the step to
/// complete. What a person would run into (a target never found, a click that does nothing, a step that does not
/// complete, a card over the target) is recorded with a screenshot, and every run ends in one HTML report.
///
/// dotnet run --project tests -- --autopilot examples/autopilot [--report DIR] [--case NAME]
///
/// A case file is {name, launch: {exe, args, process, readyTimeoutMs}, spec: {…tutorial…}}. The robot never touches
/// an app that was already open: such a case is skipped. It closes only what it started.
/// It moves the real mouse and presses real keys: nobody may use the computer while it runs.
/// </summary>
internal static class Autopilot
{
    private const int StepTimeoutMs = 20000, VisibleTimeoutMs = 8000, AfterActionMs = 4000;

    /// <summary>How long the robot looks at each step before it acts (a person reads the card first): the time the
    /// marks and the card are watched for twitching.</summary>
    private const int WatchMs = 1500;

    public static int Run(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);   // physical pixels
        if (args.Length == 0) { Console.Error.WriteLine("usage: --autopilot <case.json | folder> [--report DIR] [--case NAME]"); return 1; }
        var target = Path.GetFullPath(args[0]);
        string? only = Opt(args, "--case");
        var files = Directory.Exists(target) ? Directory.GetFiles(target, "*.json").OrderBy(f => f).ToList()
            : File.Exists(target) ? new List<string> { target } : new List<string>();
        if (only != null) files = files.Where(f => Path.GetFileNameWithoutExtension(f).Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) { Console.Error.WriteLine("no case files at " + target); return 1; }

        var reportDir = Opt(args, "--report") ?? Path.Combine(Directory.Exists(target) ? target : Path.GetDirectoryName(target)!,
            "results", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(reportDir);

        using var mcp = Mcp.Connect();
        Console.WriteLine($"server {mcp.Url} (version {mcp.ServerVersion}) · {files.Count} case(s) · report {reportDir}");
        Console.WriteLine("the robot moves the mouse and presses keys now: hands off until it is done");
        mcp.TakeTurn();

        var results = new List<CaseResult>();
        try
        {
            foreach (var f in files)
            {
                var r = RunCase(mcp, f, reportDir);
                results.Add(r);
                Console.WriteLine($"{(r.Verdict == "pass" ? "PASS" : r.Verdict == "skip" ? "SKIP" : "FAIL")}  {r.Name}" +
                                  (r.Note != null ? "  — " + r.Note : ""));
            }
        }
        finally { mcp.TryCall("release", new JsonObject()); }

        var html = Path.Combine(reportDir, "report.html");
        File.WriteAllText(html, Report(results, mcp));
        int failed = results.Count(r => r.Verdict == "fail");
        Console.WriteLine($"{results.Count(r => r.Verdict == "pass")} passed, {failed} failed, {results.Count(r => r.Verdict == "skip")} skipped · {html}");
        return failed;
    }

    private static string? Opt(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    // ================================================================ one case

    internal sealed class StepResult
    {
        public int Index;
        public string Title = "";
        public string Action = "";
        public string Verdict = "pass";          // pass | fail | skip
        public string? Note;
        public List<string> Warnings = new();
        public string? Shot;                     // file name in the report folder
        public long Ms;
    }

    internal sealed class CaseResult
    {
        public string Name = "", File = "";
        public string Verdict = "pass";
        public string? Note;
        public List<StepResult> Steps = new();
        public long Ms;
    }

    private static CaseResult RunCase(Mcp mcp, string file, string reportDir)
    {
        var res = new CaseResult { File = file, Name = Path.GetFileNameWithoutExtension(file) };
        var sw = Stopwatch.StartNew();
        JsonObject c;
        try { c = JsonNode.Parse(File.ReadAllText(file))!.AsObject(); }
        catch (Exception e) { res.Verdict = "fail"; res.Note = "unreadable case: " + e.Message; return res; }
        res.Name = J.Str(c, "name") ?? res.Name;
        var spec = c["spec"]?.AsObject();
        if (spec == null) { res.Verdict = "fail"; res.Note = "no spec"; return res; }
        spec = spec.DeepClone().AsObject();
        spec["outro"] ??= false;              // the end card would wait for a person
        var launch = c["launch"] as JsonObject;
        var process = J.Str(launch, "process") ?? J.Str(spec, "app");
        var started = new List<Process>();
        DateTime launchedAt = DateTime.Now;

        try
        {
            // ---- launch, never into an app the user has open
            if (process != null && Process.GetProcessesByName(process).Length > 0)
            {
                res.Verdict = "skip";
                res.Note = $"{process} is already open — close it first (the robot never touches an app it did not start)";
                return res;
            }
            if (J.Str(launch, "exe") is string exe)
            {
                launchedAt = DateTime.Now.AddSeconds(-1);
                Process.Start(new ProcessStartInfo(exe, J.Str(launch, "args") ?? "") { UseShellExecute = true });
            }

            // ---- ready: the first step's target resolves
            int readyMs = (int?)J.Num(launch, "readyTimeoutMs") ?? 15000;
            var ready = Stopwatch.StartNew();
            JsonNode? val = null;
            while (ready.ElapsedMilliseconds < readyMs)
            {
                val = mcp.Call("validate", new JsonObject { ["spec"] = spec.DeepClone() }).Json;
                if (J.Get(val, "steps") is JsonArray st && st.Count > 0 && FirstStepOk(st[0])) break;
                // Windows may open the app behind the window in front (it limits focus stealing): a person clicks it
                // to the front, the robot raises it the same way
                var why = val?.ToJsonString() ?? "";
                if (why.Contains("occluded") || why.Contains("inactive")) BringToFront(process, J.Str(launch, "window"));
                Thread.Sleep(700);
            }
            if (J.Get(val, "steps") is not JsonArray vs || vs.Count == 0 || !FirstStepOk(vs[0]))
            {
                res.Verdict = "fail";
                res.Note = "the first step's target never resolved: " + Short(val?.ToJsonString(), 300);
                return res;
            }
            if (J.Num(val, "errors") is > 0) res.Note = "validate: " + J.Num(val, "errors") + " error(s)";

            // ---- play
            var startAns = mcp.Call("start_tutorial", new JsonObject { ["spec"] = spec.DeepClone() });
            if (startAns.IsError) { res.Verdict = "fail"; res.Note = "start_tutorial: " + Short(startAns.Text, 300); return res; }
            long seq = (long?)J.Num(startAns.Json, "seq") ?? (long?)J.Num(mcp.Call("status", new JsonObject()).Json, "lastEventSeq") ?? 0;
            var steps = J.Items(spec["steps"]).ToList();
            for (int i = 0; i < steps.Count; i++)
            {
                var sr = PlayStep(mcp, steps[i]!.AsObject(), spec, i > 0 ? steps[i - 1]!.AsObject() : null, i, ref seq, reportDir, res.Name);
                res.Steps.Add(sr);
                if (sr.Verdict == "fail") { res.Verdict = "fail"; res.Note ??= $"step {i + 1}: {sr.Note}"; break; }
            }
        }
        catch (Exception e) { res.Verdict = "fail"; res.Note = e.GetType().Name + ": " + e.Message; }
        finally
        {
            mcp.TryCall("stop_tutorial", new JsonObject { ["outro"] = false });
            if (process != null) CloseStarted(process, launchedAt);
            res.Ms = sw.ElapsedMilliseconds;
        }
        return res;
    }

    private static bool FirstStepOk(JsonNode? step) =>
        J.Items(J.Get(step, "targets")).Any() ? J.Items(J.Get(step, "targets")).All(t => J.Get(t, "ok")?.GetValue<bool>() == true) : true;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    /// <summary>
    /// Raises the app's window: the one titled <paramref name="title"/> (apps hosted by another process, such as
    /// Calculator in ApplicationFrameHost), else the process's main window. A tap of Alt first: Windows lets a
    /// process that just had input raise another window.
    /// </summary>
    private static void BringToFront(string? process, string? title)
    {
        IntPtr h = IntPtr.Zero;
        if (title != null)
            h = Process.GetProcesses().Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                .Select(p => p.MainWindowHandle).FirstOrDefault();
        if (h == IntPtr.Zero && process != null)
            h = Process.GetProcessesByName(process).Select(p => p.MainWindowHandle).FirstOrDefault(x => x != IntPtr.Zero);
        if (h == IntPtr.Zero) return;
        if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
        keybd_event(0x12, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, 2, IntPtr.Zero);
        SetForegroundWindow(h);
    }

    /// <summary>Closes the app's windows the robot opened (started after the launch), then kills what is left.</summary>
    private static void CloseStarted(string process, DateTime since)
    {
        var mine = Process.GetProcessesByName(process).Where(p => { try { return p.StartTime >= since; } catch { return false; } }).ToList();
        foreach (var p in mine) { try { p.CloseMainWindow(); } catch { } }
        foreach (var p in mine) { try { if (!p.WaitForExit(3000)) p.Kill(true); } catch { } }
    }

    // ================================================================ one step

    private static StepResult PlayStep(Mcp mcp, JsonObject spec, JsonObject specRoot, JsonObject? prev, int index, ref long seq, string reportDir, string caseName)
    {
        var r = new StepResult { Index = index, Title = J.Str(spec, "title") ?? $"step {index + 1}" };
        var sw = Stopwatch.StartNew();
        var until = J.Items(spec["until"]).Select(u => u is JsonValue ? new JsonObject { ["type"] = u!.GetValue<string>() } : u!.AsObject()).ToList();
        string kind = until.Select(u => J.Str(u, "type")).FirstOrDefault(t => t is "key" or "scroll" or "drag" or "trace" or "count" or "manual") ?? "click";
        r.Action = kind;

        JsonObject? status = WaitForStep(mcp, index, 5000);
        if (status == null) { r.Verdict = "fail"; r.Note = "the lesson never reached this step"; return r; }

        if (kind is "count" or "manual")
        {
            r.Verdict = "skip";
            r.Note = $"the robot cannot do '{kind}' yet: completed on its behalf";
            Shoot(mcp, r, reportDir, caseName);
            mcp.Call("complete_step", new JsonObject { ["expectIndex"] = index, ["immediate"] = true });
            WaitPast(mcp, index, 3000);
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        bool shot = false;
        int actions = 0;
        while (sw.ElapsedMilliseconds < StepTimeoutMs)
        {
            // what went wrong is in the events: a target that is not where it should be, a click that did nothing
            foreach (var ev in Events(mcp, ref seq))
            {
                var e = J.Str(ev, "event");
                if (e is "target_not_found" && J.Num(ev, "step") == index)
                {
                    r.Verdict = "fail";
                    r.Note = $"target_not_found: {J.Str(ev, "anchor")} ({J.Str(ev, "reason")}, {J.Str(ev, "where")})";
                }
                else if (e is "no_effect" && (J.Num(ev, "step") ?? index) == index)
                    r.Warnings.Add("no_effect: " + Short(ev!.ToJsonString(), 160));
                else if (e is "miss" && J.Num(ev, "step") == index)
                    r.Warnings.Add($"miss at {J.Num(ev, "x")},{J.Num(ev, "y")}");
            }
            if (r.Verdict == "fail") { Shoot(mcp, r, reportDir, caseName); return r; }

            status = mcp.Call("status", new JsonObject()).Json?.AsObject();
            var tut = J.Get(status, "tutorial");
            if (Past(tut, index)) break;
            var step = J.Get(tut, "step");
            var targets = J.Items(J.Get(step, "targets")).Where(t => J.Get(t, "satisfied")?.GetValue<bool>() != true).ToList();
            var visible = targets.FirstOrDefault(t => J.Str(t, "visibility") is "visible");

            if (kind == "click" && targets.Count > 0 && visible == null)
            {
                if (sw.ElapsedMilliseconds > VisibleTimeoutMs)
                {
                    r.Verdict = "fail";
                    r.Note = "target not visible: " + string.Join("; ", targets.Select(t => $"{J.Str(t, "name") ?? J.Str(t, "id")} — {J.Str(t, "visibility")}"));
                    Shoot(mcp, r, reportDir, caseName);
                    return r;
                }
                Thread.Sleep(300);
                continue;
            }

            if (!shot)
            {
                Thread.Sleep(600);   // the marks and the card settle (the mascot flies in)
                status = Watch(mcp, WatchMs, r) ?? status;
                var tut2 = J.Get(status, "tutorial");
                if (Past(tut2, index)) break;   // completed while it watched (a condition met by itself)
                visible = J.Items(J.Get(J.Get(tut2, "step"), "targets"))
                    .FirstOrDefault(t => J.Get(t, "satisfied")?.GetValue<bool>() != true && J.Str(t, "visibility") is "visible") ?? visible;
                LookChecks(status!, visible, r);
                Shoot(mcp, r, reportDir, caseName);
                shot = true;
            }

            // done what it can; wait for the step to notice (a drawn path is tried twice, a click up to six times)
            if (actions >= (kind is "drag" or "trace" ? 2 : 6)) { Thread.Sleep(300); continue; }
            actions++;
            switch (kind)
            {
                case "key":
                    var keys = until.First(u => J.Str(u, "type") == "key");
                    var chordText = J.Get(keys, "keys") is JsonArray ka ? J.AsStr(ka[0]) : J.Str(keys, "keys") ?? J.Str(keys, "key");
                    if (TutorialOverlay.Keys.Parse(chordText) is not Chord chord) { r.Verdict = "fail"; r.Note = "unreadable keys: " + chordText; return r; }
                    // a step that marks where to press (Blender's keys act on the area under the pointer): the hand
                    // goes there first
                    if (visible != null && Center(visible) is (int hx, int hy)) Input.Hover(hx, hy);
                    Input.Chord(chord.Mods, chord.Vk);
                    break;
                case "scroll":
                    var sc = until.First(u => J.Str(u, "type") == "scroll");
                    // over the area the condition names (in/area/over/anchor), else over the target
                    var area = J.Get(sc, "in") ?? J.Get(sc, "area") ?? J.Get(sc, "over") ?? (J.Get(sc, "anchor") is JsonNode sa ? new JsonObject { ["anchor"] = sa.DeepClone() } : null);
                    var at = (area != null ? RegionPoint(mcp, area, specRoot) : null)
                             ?? (visible != null ? Center(visible) : null) ?? Center(J.Items(J.Get(step, "targets")).FirstOrDefault()) ?? ScreenCenter(status!);
                    var sdir = (J.Str(sc, "direction") ?? J.Str(sc, "dir") ?? "down").ToLowerInvariant();
                    int notches = J.Str(sc, "until") == "visible" ? 5 : (int?)J.Num(sc, "notches") ?? (int?)J.Num(sc, "count") ?? 3;
                    Input.Wheel(at.x, at.y, (sdir is "up" or "left" ? 1 : -1) * 120 * notches, sdir is "left" or "right");
                    break;
                case "drag":
                    var dg = until.First(u => J.Str(u, "type") == "drag");
                    var p0 = RegionPoint(mcp, J.Get(dg, "from"), specRoot);
                    var p1 = RegionPoint(mcp, J.Get(dg, "to"), specRoot);
                    if (p0 == null || p1 == null) { r.Verdict = "fail"; r.Note = $"drag: {(p0 == null ? "from" : "to")} does not resolve"; Shoot(mcp, r, reportDir, caseName); return r; }
                    Input.Drag(new List<(int, int)> { p0.Value, p1.Value }, J.Str(dg, "button") ?? "left", HoldKeys(dg));
                    break;
                case "trace":
                    var tr = until.First(u => J.Str(u, "type") == "trace");
                    var anchorNode = J.Get(tr, "anchor") ?? StripRegion(tr);
                    var basePt = AnchorPoint(mcp, anchorNode, specRoot);
                    if (basePt == null) { r.Verdict = "fail"; r.Note = "trace: its anchor does not resolve"; Shoot(mcp, r, reportDir, caseName); return r; }
                    var pts = Condition.ParsePoints(J.Get(tr, "path") ?? J.Get(tr, "points"))
                        .Select(p => ((int)Math.Round(basePt.Value.x + p.X), (int)Math.Round(basePt.Value.y + p.Y))).ToList();
                    Input.Drag(pts, J.Str(tr, "button") ?? "left", HoldKeys(tr));
                    break;
                default:
                    if (visible == null) { Thread.Sleep(300); continue; }   // a step with no target waits for its condition
                    var (x, y) = Center(visible)!.Value;
                    // the way there matters in menus: many open a submenu on hover, so a hand that cuts across the
                    // neighbours opens theirs. Out of the menu bar a person goes down first (sideways along the bar
                    // opens the next menu), into a submenu right first along its row, then down.
                    bool inMenu = J.Get(spec, "inMenu")?.GetValue<bool>() == true;
                    bool fromMenu = J.Get(prev, "inMenu")?.GetValue<bool>() == true;
                    var way = !inMenu ? Input.Way.Straight : fromMenu ? Input.Way.AcrossThenDown : Input.Way.DownThenAcross;
                    if (way != Input.Way.Straight) r.Action = "click · " + (way == Input.Way.AcrossThenDown ? "into a submenu" : "into a menu");
                    // a person aims at where the mark is when they press, not where it was a moment ago: hover, look
                    // again, correct the aim. A mark that moved meanwhile, with nothing moving the app, is twitching.
                    Input.Hover(x, y, way);
                    Input.Pause();
                    var targetId = J.Str(visible, "id");
                    var tutNow = J.Get(mcp.Call("status", new JsonObject()).Json, "tutorial");
                    // a submenu that opens on hover: pointing was the whole step, as for a person
                    if (Past(tutNow, index)) { r.Action += " · done on hover, no click"; break; }
                    var now = J.Items(J.Get(J.Get(tutNow, "step"), "targets"))
                        .FirstOrDefault(t => J.Str(t, "id") == targetId && J.Str(t, "visibility") is "visible");
                    if (now != null && Center(now) is (int nx, int ny))
                    {
                        // a frame that grew to the control the hover lit up (a menu row) holds the old one: not a twitch
                        bool grew = Bounds(visible) is { } ob && Bounds(now) is { } nb2
                                    && ob.X >= nb2.X - 3 && ob.Y >= nb2.Y - 3 && ob.Right <= nb2.Right + 3 && ob.Bottom <= nb2.Bottom + 3;
                        if (grew && (Math.Abs(nx - x) > 4 || Math.Abs(ny - y) > 4)) r.Action += " · frame grew to the lit row";
                        else if (Math.Abs(nx - x) > 4 || Math.Abs(ny - y) > 4)
                            r.Warnings.Add($"the mark of {J.Str(visible, "name") ?? targetId} moved from {x},{y} to {nx},{ny} while the hand was on it (twitching)");
                        (x, y) = (nx, ny);
                    }
                    Input.ClickHere(x, y, TargetButton(spec, visible));
                    break;
            }
            WaitPast(mcp, index, AfterActionMs);
        }

        status = mcp.Call("status", new JsonObject()).Json?.AsObject();
        if (!Past(J.Get(status, "tutorial"), index))
        {
            r.Verdict = "fail";
            r.Note = $"the step did not complete after {actions} action(s) in {StepTimeoutMs / 1000} s" +
                     (r.Warnings.Count > 0 ? " (" + string.Join("; ", r.Warnings) + ")" : "");
            Shoot(mcp, r, reportDir, caseName);
        }
        r.Ms = sw.ElapsedMilliseconds;
        return r;
    }

    // ---- where a condition's places are: the server resolves them, exactly as the lesson will

    /// <summary>
    /// The resolved point and bounds of an anchor: a one-step lesson with it as the target, validated by the server
    /// (with the lesson's own defaults, such as its app).
    /// </summary>
    private static (double[]? point, double[]? bounds)? Resolve(Mcp mcp, JsonNode? anchor, JsonObject specRoot)
    {
        if (anchor == null) return null;
        var probe = new JsonObject { ["title"] = "probe", ["steps"] = new JsonArray(new JsonObject
        {
            ["title"] = "probe", ["targets"] = new JsonArray(new JsonObject { ["anchor"] = anchor.DeepClone() }),
        }) };
        foreach (var k in new[] { "app", "process", "window", "anchor" })
            if (specRoot[k] is JsonNode v) probe[k] = v.DeepClone();
        var t = J.Items(J.Get(J.Items(J.Get(mcp.Call("validate", new JsonObject { ["spec"] = probe }).Json, "steps")).FirstOrDefault(), "targets")).FirstOrDefault();
        static double[]? Arr(JsonNode? n) => n is JsonArray a && a.Count >= 2 ? a.Select(x => (double)x!).ToArray() : null;
        var pt = Arr(J.Get(t, "point"));
        return pt == null ? null : (pt, Arr(J.Get(t, "bounds")));
    }

    private static (int x, int y)? AnchorPoint(Mcp mcp, JsonNode? anchor, JsonObject specRoot) =>
        Resolve(mcp, anchor, specRoot) is { point: double[] p } ? ((int)Math.Round(p[0]), (int)Math.Round(p[1])) : null;

    /// <summary>A region's own anchor fields, without its rect / radius (they are the region's, not the anchor's).</summary>
    private static JsonObject StripRegion(JsonNode n)
    {
        var o = n.DeepClone().AsObject();
        foreach (var k in new[] { "rect", "radius", "type", "path", "points", "button", "hold", "holding", "smooth", "ordered", "tolerance", "coverage", "show" }) o.Remove(k);
        return o;
    }

    /// <summary>
    /// Where the hand goes for a drag's from / to or a scroll's area, as the server checks it: the rect's centre
    /// (rect is relative to the anchor point), the anchor point (radius, or nothing to frame), else the centre of
    /// what the anchor frames.
    /// </summary>
    private static (int x, int y)? RegionPoint(Mcp mcp, JsonNode? region, JsonObject specRoot)
    {
        if (region == null) return null;
        var anchor = J.Get(region, "anchor") ?? StripRegion(region);
        if (Resolve(mcp, anchor, specRoot) is not { point: double[] p } res) return null;
        if (J.Get(region, "rect") is JsonArray rr && rr.Count >= 4)
            return ((int)Math.Round(p[0] + (double)rr[0]! + (double)rr[2]! / 2), (int)Math.Round(p[1] + (double)rr[1]! + (double)rr[3]! / 2));
        if (J.Num(region, "radius") != null || res.bounds is not { Length: >= 4 } b) return ((int)Math.Round(p[0]), (int)Math.Round(p[1]));
        return ((int)Math.Round(b[0] + b[2] / 2), (int)Math.Round(b[1] + b[3] / 2));
    }

    /// <summary>"hold": ["Shift"] / "Ctrl+Alt": the keys the drag must keep down, as virtual-key codes.</summary>
    private static List<uint> HoldKeys(JsonNode cond)
    {
        var res = new List<uint>();
        foreach (var k in J.Items(J.Get(cond, "hold") ?? J.Get(cond, "holding")))
            foreach (var part in (J.AsStr(k) ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                uint vk = part.ToLowerInvariant() switch
                {
                    "shift" => 0x10, "ctrl" or "control" => 0x11, "alt" => 0x12, "win" => 0x5B, "space" => 0x20,
                    _ => TutorialOverlay.Keys.Parse(part)?.Vk ?? 0,
                };
                if (vk != 0) res.Add(vk);
            }
        return res;
    }

    /// <summary>The mouse button a target wants ("button": left | right | middle | any), from the lesson.</summary>
    private static string TargetButton(JsonObject step, JsonNode target)
    {
        var id = J.Str(target, "id") ?? "";
        var list = J.Items(step["targets"]).ToList();
        var spec = list.FirstOrDefault(t => J.Str(t, "id") == id);
        if (spec == null && System.Text.RegularExpressions.Regex.Match(id, @"t(\d+)$") is { Success: true } m && int.Parse(m.Groups[1].Value) < list.Count)
            spec = list[int.Parse(m.Groups[1].Value)];
        var b = (J.Str(spec, "button") ?? "left").ToLowerInvariant();
        return b is "right" or "middle" ? b : "left";
    }

    /// <summary>The lesson is past this step: a later index, or the lesson is over.</summary>
    private static bool Past(JsonNode? tut, int index)
    {
        var state = J.Str(tut, "state");
        if (state is "finished" or "stopped" or "none" or "done" or "outro" or "waiting") return true;
        return (J.Num(tut, "index") ?? -1) > index || J.Get(tut, "completing")?.GetValue<bool>() == true && (J.Num(tut, "index") ?? -1) == index;
    }

    private static JsonObject? WaitForStep(Mcp mcp, int index, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var s = mcp.Call("status", new JsonObject()).Json?.AsObject();
            var tut = J.Get(s, "tutorial");
            if (J.Num(tut, "index") == index && J.Str(tut, "state") == "running" && J.Get(tut, "completing")?.GetValue<bool>() != true) return s;
            Thread.Sleep(200);
        }
        return null;
    }

    private static void WaitPast(Mcp mcp, int index, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (Past(J.Get(mcp.Call("status", new JsonObject()).Json, "tutorial"), index)) return;
            Thread.Sleep(200);
        }
    }

    private static List<JsonNode?> Events(Mcp mcp, ref long seq)
    {
        var ans = mcp.Call("events", new JsonObject { ["since"] = seq, ["timeoutMs"] = 0 }).Json;
        var list = J.Items(J.Get(ans, "events")).Select(e => (JsonNode?)e).ToList();
        foreach (var e in list) seq = Math.Max(seq, (long)(J.Num(e, "seq") ?? seq));
        return list;
    }

    /// <summary>
    /// Looks at the step for a while before acting, as a person reads the card first, and samples where the marks and
    /// the card are. Nothing moves the app meanwhile, so a mark or the card that moves, or a target that blinks
    /// between visible and hidden, is the overlay twitching. Returns the last status.
    /// </summary>
    private static JsonObject? Watch(Mcp mcp, int ms, StepResult r)
    {
        var marks = new Dictionary<string, List<double[]>>();
        var card = new List<double[]>();
        var vis = new Dictionary<string, List<string>>();
        JsonObject? last = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            last = mcp.Call("status", new JsonObject()).Json?.AsObject();
            foreach (var t in J.Items(J.Get(J.Get(J.Get(last, "tutorial"), "step"), "targets")))
            {
                var id = J.Str(t, "id") ?? "?";
                var v = J.Str(t, "visibility") ?? "";
                if (!vis.TryGetValue(id, out var vl)) vis[id] = vl = new List<string>();
                if (vl.Count == 0 || vl[^1] != v) vl.Add(v);
                if (J.Get(t, "bounds") is JsonArray b && b.Count == 4)
                {
                    if (!marks.TryGetValue(id, out var ml)) marks[id] = ml = new List<double[]>();
                    ml.Add(b.Select(x => (double)x!).ToArray());
                }
            }
            if (J.Get(last, "card") is JsonArray c && c.Count == 4) card.Add(c.Select(x => (double)x!).ToArray());
            Thread.Sleep(100);
        }
        static double Spread(List<double[]> l) =>
            l.Count < 2 ? 0 : Enumerable.Range(0, 4).Max(i => l.Max(a => a[i]) - l.Min(a => a[i]));
        foreach (var (id, l) in marks)
            if (Spread(l) > 2) r.Warnings.Add($"the mark of {id} moved {Spread(l):0} px while nothing moved the app (twitching)");
        if (Spread(card) > 2) r.Warnings.Add($"the card moved {Spread(card):0} px while it should rest");
        foreach (var (id, l) in vis)
            if (l.Count > 1) r.Warnings.Add($"{id} blinked between {string.Join(" → ", l)}");
        return last;
    }

    /// <summary>What a person would see wrong: a target off screen, the card over the target.</summary>
    private static void LookChecks(JsonObject status, JsonNode? target, StepResult r)
    {
        if (target == null || J.Get(target, "bounds") is not JsonArray b || b.Count < 4) return;
        var t = (x: (double)b[0]!, y: (double)b[1]!, w: (double)b[2]!, h: (double)b[3]!);
        if (J.Get(status, "virtualScreen") is JsonArray vs && vs.Count == 4)
        {
            double vx = (double)vs[0]!, vy = (double)vs[1]!, vw = (double)vs[2]!, vh = (double)vs[3]!;
            if (t.x < vx || t.y < vy || t.x + t.w > vx + vw || t.y + t.h > vy + vh) r.Warnings.Add("the target is partly off screen");
        }
        if (J.Get(status, "card") is JsonArray cr && cr.Count == 4)
        {
            double cx = (double)cr[0]!, cy = (double)cr[1]!, cw = (double)cr[2]!, ch = (double)cr[3]!;
            double ix = Math.Max(0, Math.Min(t.x + t.w, cx + cw) - Math.Max(t.x, cx)), iy = Math.Max(0, Math.Min(t.y + t.h, cy + ch) - Math.Max(t.y, cy));
            if (ix * iy > 0.2 * t.w * t.h) r.Warnings.Add("the card covers the target");
        }
    }

    private static (int x, int y)? Center(JsonNode? target) =>
        J.Get(target, "center") is JsonArray c && c.Count >= 2 ? ((int)(double)c[0]!, (int)(double)c[1]!) : null;

    private static System.Drawing.RectangleF? Bounds(JsonNode? target) =>
        J.Get(target, "bounds") is JsonArray b && b.Count == 4
            ? new System.Drawing.RectangleF((float)(double)b[0]!, (float)(double)b[1]!, (float)(double)b[2]!, (float)(double)b[3]!) : null;

    private static (int x, int y) ScreenCenter(JsonObject status) =>
        J.Get(status, "virtualScreen") is JsonArray v && v.Count == 4
            ? ((int)((double)v[0]! + (double)v[2]! / 2), (int)((double)v[1]! + (double)v[3]! / 2)) : (500, 500);

    /// <summary>A picture of the screen with the overlay's marks, card and mascot, for the report.</summary>
    private static void Shoot(Mcp mcp, StepResult r, string dir, string caseName)
    {
        try
        {
            var st = mcp.Call("status", new JsonObject()).Json;
            var vs = J.Get(st, "virtualScreen")?.DeepClone() ?? new JsonArray(0, 0, 1920, 1080);
            var ans = mcp.Call("screenshot", new JsonObject { ["rect"] = vs, ["includeOverlay"] = true, ["maxEdge"] = 1280 });
            if (ans.Image == null) return;
            var name = $"{Safe(caseName)}-{r.Index + 1:00}-{(r.Verdict == "fail" ? "fail" : "step")}.png";
            File.WriteAllBytes(Path.Combine(dir, name), ans.Image);
            r.Shot = name;
        }
        catch { /* a missing picture never fails a case */ }
    }

    private static string Safe(string s) => new(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
    private static string Short(string? s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + "…";

    // ================================================================ report

    private static string Report(List<CaseResult> results, Mcp mcp)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Autopilot report</title><style>")
          .Append(":root{color-scheme:light dark;--bg:#fff;--fg:#1b1d21;--dim:#6a6f78;--line:#d9dce1;--ok:#1a7f37;--bad:#c62828;--skip:#9a6700}")
          .Append("@media(prefers-color-scheme:dark){:root{--bg:#16181c;--fg:#e6e8eb;--dim:#9aa0a8;--line:#2c3036;--ok:#4cc26a;--bad:#ff6b6b;--skip:#e3b341}}")
          .Append("body{margin:0;padding:24px 16px;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,'Segoe UI',sans-serif;max-width:1100px;margin:auto}")
          .Append("h1{font-size:20px;margin:0 0 4px}.meta{color:var(--dim);margin-bottom:20px}")
          .Append("details{border:1px solid var(--line);border-radius:6px;margin:10px 0;padding:8px 12px}summary{cursor:pointer;font-weight:600}")
          .Append(".pass{color:var(--ok)}.fail{color:var(--bad)}.skip{color:var(--skip)}.note{color:var(--dim);font-weight:400}")
          .Append(".step{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,360px);gap:12px;border-top:1px solid var(--line);padding:10px 0}")
          .Append(".step img{width:100%;border:1px solid var(--line);border-radius:4px}ul{margin:4px 0;padding-left:18px}")
          .Append("@media(max-width:700px){.step{grid-template-columns:1fr}}</style>");
        int pass = results.Count(r => r.Verdict == "pass"), fail = results.Count(r => r.Verdict == "fail"), skip = results.Count(r => r.Verdict == "skip");
        sb.Append($"<h1>Autopilot: {pass} passed, {fail} failed, {skip} skipped</h1>")
          .Append($"<div class=meta>{DateTime.Now:yyyy-MM-dd HH:mm} · server {H(mcp.Url)} · version {H(mcp.ServerVersion)}</div>");
        foreach (var c in results)
        {
            sb.Append($"<details{(c.Verdict == "fail" ? " open" : "")}><summary><span class={c.Verdict}>{c.Verdict.ToUpperInvariant()}</span> {H(c.Name)} ")
              .Append($"<span class=note>· {c.Steps.Count} step(s) · {c.Ms / 1000.0:0.0} s{(c.Note != null ? " · " + H(c.Note) : "")}</span></summary>");
            foreach (var s in c.Steps)
            {
                sb.Append($"<div class=step><div><b class={s.Verdict}>{s.Index + 1}. {H(s.Title)}</b> <span class=note>({H(s.Action)}, {s.Ms} ms)</span>");
                if (s.Note != null) sb.Append($"<div>{H(s.Note)}</div>");
                if (s.Warnings.Count > 0) sb.Append("<ul>").Append(string.Concat(s.Warnings.Select(w => $"<li>{H(w)}</li>"))).Append("</ul>");
                sb.Append("</div>");
                sb.Append(s.Shot != null ? $"<a href='{H(s.Shot)}'><img src='{H(s.Shot)}' alt='step {s.Index + 1}'></a>" : "<div></div>");
                sb.Append("</div>");
            }
            sb.Append("</details>");
        }
        return sb.ToString();
    }

    // ================================================================ the MCP client

    internal sealed class Answer
    {
        public JsonNode? Json;
        public string Text = "";
        public byte[]? Image;
        public bool IsError;
    }

    internal sealed class Mcp : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
        private string? _session;
        private int _id;
        public string Url = "", ServerVersion = "?";

        public static Mcp Connect()
        {
            var dir = Files.AppDir;
            var server = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "server.json")));
            var key = Environment.GetEnvironmentVariable("TUTORIAL_OVERLAY_KEY") ?? File.ReadAllText(Path.Combine(dir, "key.txt")).Trim();
            var m = new Mcp { Url = J.Str(server, "url") ?? $"http://127.0.0.1:{J.Num(server, "port")}/mcp", ServerVersion = J.Str(server, "version") ?? "?" };
            m._http.DefaultRequestHeaders.Add("X-Overlay-Key", key);
            var init = m.Rpc("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "autopilot", ["version"] = "1" },
            });
            if (J.Str(J.Get(init, "serverInfo"), "version") is string v) m.ServerVersion = v;
            m.Notify("notifications/initialized");
            return m;
        }

        /// <summary>Waits for the server if another agent holds it (a Claude session that ran a lesson).</summary>
        public void TakeTurn()
        {
            var a = Call("status", new JsonObject());
            if (!a.IsError || !a.Text.StartsWith("busy")) return;
            Console.WriteLine("the server is held by another agent: waiting for it (up to 2 min)");
            var w = Call("wait_for_turn", new JsonObject { ["timeoutMs"] = 120000 });
            if (J.Get(w.Json, "granted")?.GetValue<bool>() != true) throw new Exception("the server stayed busy: " + w.Text);
        }

        public Answer Call(string tool, JsonObject args)
        {
            var result = Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });
            var a = new Answer { IsError = J.Get(result, "isError")?.GetValue<bool>() == true };
            foreach (var part in J.Items(J.Get(result, "content")))
            {
                if (J.Str(part, "type") == "text") a.Text += J.Str(part, "text");
                else if (J.Str(part, "type") == "image" && J.Str(part, "data") is string d) a.Image = Convert.FromBase64String(d);
            }
            try { a.Json = a.Text.Length > 0 && a.Text[0] is '{' or '[' ? JsonNode.Parse(a.Text) : null; } catch { }
            return a;
        }

        public void TryCall(string tool, JsonObject args)
        {
            try { Call(tool, args); } catch { }
        }

        private void Notify(string method)
        {
            using var req = Request(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });
            _http.Send(req).Dispose();
        }

        private JsonNode? Rpc(string method, JsonObject prms)
        {
            int id = Interlocked.Increment(ref _id);
            using var req = Request(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms });
            using var resp = _http.Send(req);
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _session ??= sid.First();
            var body = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
            JsonNode? msg = null;
            if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                foreach (var line in body.Split('\n'))
                    if (line.StartsWith("data:") && JsonNode.Parse(line[5..].Trim()) is JsonNode n && J.Num(n, "id") == id) msg = n;
            }
            else if (body.Length > 0) msg = JsonNode.Parse(body);
            if (!resp.IsSuccessStatusCode && msg == null) throw new Exception($"{method}: HTTP {(int)resp.StatusCode} {Short(body, 200)}");
            if (J.Get(msg, "error") is JsonNode err) throw new Exception($"{method}: {J.Str(err, "message")}");
            return J.Get(msg, "result");
        }

        private HttpRequestMessage Request(JsonObject body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            if (_session != null) req.Headers.Add("Mcp-Session-Id", _session);
            return req;
        }

        public void Dispose()
        {
            try
            {
                if (_session != null)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Delete, Url);
                    req.Headers.Add("Mcp-Session-Id", _session);
                    _http.Send(req).Dispose();
                }
            }
            catch { }
            _http.Dispose();
        }
    }

    // ================================================================ the robot's hands

    private static class Input
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);

        private const uint MOUSE = 0, KEYBOARD = 1;
        private const uint MOVE = 0x0001, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
        private const uint WHEEL = 0x0800, HWHEEL = 0x1000, KEYUP = 0x02, EXTENDED = 0x01;

        /// <summary>Down / up flags of a mouse button.</summary>
        private static (uint down, uint up) Button(string b) => b switch
        {
            "right" => (0x08, 0x10),
            "middle" => (0x20, 0x40),
            _ => (0x02, 0x04),
        };

        /// <summary>
        /// Keys whose code is shared with the number pad: sent without the "extended" flag, Delete arrives as the
        /// pad's "." (Blender: View Selected), Home as the pad's 7, an arrow as the pad's digit.
        /// </summary>
        private static bool Extended(uint vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0xA3 or 0xA5;

        /// <summary>A person looks before acting: the pause after the hand arrives (or before keys), so hover
        /// effects, the mascot and the card react as they would to a real user.</summary>
        private const int HumanPauseMs = 1000;   // on top of WatchMs: about 2.5 s per step

        private static void Send(params INPUT[] i) => SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>());
        private static INPUT M(uint flags, uint data = 0) => new() { type = MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } };
        private static INPUT K(uint vk, bool up) => new()
        {
            type = KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = (up ? KEYUP : 0) | (Extended(vk) ? EXTENDED : 0) } },
        };

        /// <summary>Moves there like a hand (a few steps, so hover effects and the mascot's eyes see it) and clicks
        /// with the given button (left, right, middle).</summary>
        public static void Click(int x, int y, string button = "left")
        {
            var (down, up) = Button(button);
            MoveTo(x, y);
            Thread.Sleep(HumanPauseMs);
            Send(M(down));
            Thread.Sleep(60);
            Send(M(up));
        }

        /// <summary>
        /// Presses a button at the first point, keeps the hold keys and the button down while the hand follows the
        /// points (densely, as a hand moves: every few pixels), and lets go at the last one.
        /// </summary>
        public static void Drag(List<(int x, int y)> points, string button, List<uint> hold)
        {
            if (points.Count == 0) return;
            var (down, up) = Button(button.ToLowerInvariant());
            MoveTo(points[0].x, points[0].y);
            Thread.Sleep(HumanPauseMs);
            foreach (var k in hold) { Send(K(k, false)); Thread.Sleep(40); }
            Send(M(down));
            Thread.Sleep(120);   // a press, then motion: apps tell a drag from a click by it
            var (px, py) = points[0];
            foreach (var (x, y) in points.Skip(1))
            {
                double len = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                int n = Math.Max(1, (int)(len / 6));
                for (int i = 1; i <= n; i++)
                {
                    MoveRaw(px + (x - px) * i / n, py + (y - py) * i / n);
                    Thread.Sleep(8);
                }
                (px, py) = (x, y);
            }
            Thread.Sleep(120);
            Send(M(up));
            foreach (var k in Enumerable.Reverse(hold)) { Thread.Sleep(40); Send(K(k, true)); }
        }

        public enum Way { Straight, DownThenAcross, AcrossThenDown }

        /// <summary>Moves the hand there, straight or in two legs (see <see cref="Way"/>), without pressing.</summary>
        public static void Hover(int x, int y, Way way = Way.Straight)
        {
            Native.GetCursorPos(out var from);
            if (way == Way.DownThenAcross) MoveTo(from.X, y);
            else if (way == Way.AcrossThenDown) MoveTo(x, from.Y);
            MoveTo(x, y);
        }

        public static void Pause() => Thread.Sleep(HumanPauseMs);

        /// <summary>A click right away, after the hand has already looked (a short move if the aim changed).</summary>
        public static void ClickHere(int x, int y, string button = "left")
        {
            var (down, up) = Button(button);
            MoveTo(x, y);
            Thread.Sleep(80);
            Send(M(down));
            Thread.Sleep(60);
            Send(M(up));
        }

        public static void Wheel(int x, int y, int delta, bool horizontal)
        {
            MoveTo(x, y);
            Thread.Sleep(HumanPauseMs);
            int notches = Math.Abs(delta) / 120;
            for (int i = 0; i < notches; i++)
            {
                Send(M(horizontal ? HWHEEL : WHEEL, unchecked((uint)(Math.Sign(delta) * 120))));
                Thread.Sleep(90);
            }
        }

        public static void Chord(uint mods, uint vk)
        {
            var down = new List<uint>();
            if ((mods & Native.MOD_CONTROL) != 0) down.Add(0x11);
            if ((mods & Native.MOD_ALT) != 0) down.Add(0x12);
            if ((mods & Native.MOD_SHIFT) != 0) down.Add(0x10);
            if ((mods & Native.MOD_WIN) != 0) down.Add(0x5B);
            Thread.Sleep(HumanPauseMs);
            foreach (var k in down) { Send(K(k, false)); Thread.Sleep(30); }
            Send(K(vk, false));
            Thread.Sleep(60);
            Send(K(vk, true));
            foreach (var k in Enumerable.Reverse(down)) { Thread.Sleep(30); Send(K(k, true)); }
        }

        private static void MoveTo(int x, int y)
        {
            Native.GetCursorPos(out var from);
            for (int i = 1; i <= 8; i++)
            {
                MoveRaw(from.X + (x - from.X) * i / 8, from.Y + (y - from.Y) * i / 8);
                Thread.Sleep(15);
            }
        }

        /// <summary>
        /// A real mouse move (SendInput, absolute on the virtual desktop). SetCursorPos would put the pointer there
        /// too, but no input passes the hooks: the overlay would never see a drag or a stroke being drawn.
        /// </summary>
        private static void MoveRaw(int x, int y)
        {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            int ax = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
            int ay = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
            Send(new INPUT { type = MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = MOVE | ABSOLUTE | VIRTUALDESK } } });
        }
    }
}
