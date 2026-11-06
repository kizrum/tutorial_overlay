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
    public string? Why;
    public int Done, Required;
    public bool Completed;
    public bool Prep;                    // step 0: getting the app ready (a dialog to answer) before step 1 begins
    public bool CanBack, CanUndo;
    public string? Hint, HintTitle, Issue;
    public bool HelpPending, Highlighting, Review;
    public bool Checking;                // a target was not in the window that opened: the agent is looking for it
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
internal static partial class Engine
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
    private static readonly Dictionary<int, (string name, long at)> ProcNames = new();
    /// <summary>PIDs are reused by Windows: a cached name is trusted only this long.</summary>
    private const int ProcNameTtlMs = 5000;
    private static string _lastSignature = "";

    public static Tutorial? Tutorial => _tut;

    /// <summary>Where the step card currently is (boxed Rectangle, set by the UI thread; null = hidden).</summary>
    public static volatile object? CardRect;

    // =========================================================== free marks (draw / clear)

    public static List<string> Draw(JsonNode args)
    {
        lock (Sync)
        {
            var ids = new List<string>();
            var defaults = J.Has(args, "anchor") ? AnchorSpec.Parse(J.Get(args, "anchor"), _tut?.Defaults) : _tut?.Defaults;
            var markNodes = J.Items(J.Get(args, "marks")).ToList();
            if (markNodes.Count == 0) markNodes.Add(args);
            foreach (var n in markNodes.SelectMany(Grid.Expand))
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
            var args0 = spec;
            spec = J.Unwrap(J.Get(spec, "spec")) ?? spec;
            CheckStartAllowed(J.Bool(args0, "replace") == true || J.Bool(spec, "replace") == true);
            string? savedAs = null;
            if (J.Str(spec, "name") is string libName && !J.Has(spec, "steps"))
            {
                var startAt = J.Get(spec, "startAt")?.DeepClone();
                spec = Tutorials.Load(libName);
                if (startAt != null) spec["startAt"] = startAt;
                savedAs = Tutorials.Slug(libName);
            }
            if (spec is not JsonObject) throw new ArgumentException("spec must be an object {title, app, steps:[…]}");
            spec = (JsonObject)spec.DeepClone();
            var warnings = SpecLint.CheckSpec(spec);
            // templates carried by a saved tutorial; pixel offsets measured at another display scale
            int templates = TemplateStore.LoadFrom(spec);
            var defaults0 = DefaultsOf(spec);
            var scaleNote = Rescale((JsonObject)spec, defaults0);
            ApplySettings(Settings.Global.Clone().Apply(spec));
            var t = new Tutorial
            {
                Title = J.Str(spec, "title") ?? "",
                KeepOpen = J.Bool(spec, "keepOpen") ?? false,
                Toggle = J.Bool(spec, "toggle") ?? false,
                Dim = J.Num(spec, "dim") is double d ? (float)d : null,
                AdvanceDelayMs = J.Int(spec, "advanceDelayMs") ?? 450,
                HideWhenInactive = J.Bool(spec, "hideWhenInactive") ?? Settings.Current.HideWhenInactive ?? HideWhenInactiveDefault,
                Outro = J.Bool(spec, "outro") ?? true,
                Mascot = J.Bool(spec, "mascot") ?? true,
                Spec = (JsonObject)spec.DeepClone(),
                SavedAs = savedAs,
            };
            t.Spec.Remove("startAt");
            t.Spec.Remove("replace");
            t.Defaults = defaults0;
            t.Owner = Caller.Value;
            int i = 0;
            foreach (var s in J.Items(J.Get(spec, "steps"))) t.Steps.Add(Step.Parse(s, t.Defaults, i++));
            if (_tut is { Active: true })
            {
                // tell the previous tutorial's agent who took it over (the event goes to every agent)
                bool other = _tut.Owner != t.Owner;
                Events.Emit("tutorial_stopped", new()
                {
                    ["title"] = _tut.Title, ["reason"] = "replaced", ["by"] = other ? (t.Owner == null ? "user" : "another agent") : null,
                    ["newTitle"] = t.Title,
                });
            }
            _tut = t;
            Events.Emit("tutorial_started", new() { ["title"] = t.Title, ["total"] = t.Steps.Count, ["app"] = t.Defaults.Process });
            int start = Math.Clamp(J.Int(spec, "startAt") ?? 0, 0, Math.Max(0, t.Steps.Count - 1));
            if (t.Steps.Count == 0) Finish(); else Enter(start);
            var res = TutorialJson();
            if (warnings.Count > 0) res["warnings"] = Warnings(warnings);
            if (scaleNote != null) res["scale"] = scaleNote;
            if (templates > 0) res["templatesLoaded"] = templates;
            return res;
        }
    }

    private static JsonArray Warnings(List<string> w) =>
        new(w.Take(30).Select(x => (JsonNode)x).Concat(w.Count > 30 ? new JsonNode[] { $"… and {w.Count - 30} more" } : Array.Empty<JsonNode>()).ToArray());

    /// <summary>The tutorial-wide default anchor: spec.anchor, or spec.app / spec.process (+ spec.window).</summary>
    public static AnchorSpec DefaultsOf(JsonNode? spec)
    {
        var d = AnchorSpec.Parse(J.Get(spec, "anchor") ?? new JsonObject { ["process"] = J.Str(spec, "app") ?? J.Str(spec, "process") }, null);
        if (J.Str(spec, "window") is string w && d.Window == null) d.Window = w;
        return d;
    }

    /// <summary>Display scale of the monitor the app's main (largest) window is on; null when the app is not running.</summary>
    public static float? AppScale(AnchorSpec defaults)
    {
        if (defaults.IsScreen) return null;
        var a = defaults.WithoutFind();
        a.Path = new List<ElementSpec>();
        var wins = Resolver.MatchingWindows(a);
        if (wins.Count == 0) return null;
        var main = wins.Select(h => Native.FrameRect(h)).OrderByDescending(r => (long)r.Width * r.Height).First();
        return Native.ScaleAt(new PointF(main.X + main.Width / 2f, main.Y + main.Height / 2f));
    }

    /// <summary>Saved specs remember the display scale their pixel offsets were measured at.</summary>
    public static void StampScale(JsonObject spec, AnchorSpec defaults)
    {
        if (J.Num(spec, "scale") != null) return;
        if (AppScale(defaults) is float s) spec["scale"] = Math.Round(s, 3);
    }

    /// <summary>Converts the spec's pixel fields when it was made at another display scale; a note for the result, or null.</summary>
    private static string? Rescale(JsonObject spec, AnchorSpec defaults)
    {
        if (J.Num(spec, "scale") is not double was || was <= 0) return null;
        if (AppScale(defaults) is not float now) return $"made at scale {was:0.##}; the app is not running, so offsets are used as they are";
        double k = now / was;
        if (Math.Abs(k - 1) < 0.02) return null;
        int n = SpecScale.Apply(J.Get(spec, "steps"), k) + SpecScale.Apply(J.Get(spec, "anchor"), k);
        spec["scale"] = Math.Round(now, 3);
        Log.Info($"spec made at scale {was:0.##}, display is {now:0.##}: {n} pixel values ×{k:0.###}");
        return $"made at scale {was:0.##}, display is {now:0.##}: {n} pixel offsets multiplied by {k:0.###}";
    }

    public static JsonObject AppendSteps(JsonNode args)
    {
        lock (Sync)
        {
            if (_tut == null || !_tut.Active) throw new InvalidOperationException("no active tutorial (start_tutorial first)");
            RequireOwned();
            int i = _tut.Steps.Count, added = 0;
            int? insertAt = J.Int(args, "insertAt");
            var raw = J.Items(J.Get(args, "steps") ?? args).Select(x => x.DeepClone()).ToList();
            var warnings = SpecLint.CheckSteps(new JsonArray(raw.Select(x => x.DeepClone()).ToArray()), i);
            // steps measured at another display scale (args.scale) are converted to the tutorial's
            if (J.Num(_tut.Spec, "scale") is double now && J.Num(args, "scale") is double was && was > 0 && Math.Abs(now / was - 1) >= 0.02)
                foreach (var r in raw) SpecScale.Apply(r, now / was);
            var parsed = raw.Select(s => Step.Parse(s, _tut.Defaults, i++)).ToList();
            if (_tut.Spec["steps"] is not JsonArray specSteps) _tut.Spec["steps"] = specSteps = new JsonArray();
            if (insertAt is int at && at > _tut.Index && at <= _tut.Steps.Count)
            {
                _tut.Steps.InsertRange(at, parsed);
                for (int k = 0; k < raw.Count; k++) specSteps.Insert(Math.Min(at + k, specSteps.Count), raw[k].DeepClone());
            }
            else
            {
                _tut.Steps.AddRange(parsed);
                foreach (var r in raw) specSteps.Add(r.DeepClone());
            }
            added = parsed.Count;
            Events.Emit("steps_appended", new() { ["added"] = added, ["total"] = _tut.Steps.Count });
            if (_tut.State == TutorialState.Waiting && added > 0) { _tut.State = TutorialState.Running; Enter(_tut.Index + 1); }
            _stepVersion++;
            var res = TutorialJson();
            if (warnings.Count > 0) res["warnings"] = Warnings(warnings);
            return res;
        }
    }

    public static JsonObject GotoStep(JsonNode args)
    {
        lock (Sync)
        {
            var t = RequireOwned();
            int idx = J.Str(args, "id") is string id ? t.Steps.FindIndex(s => s.Id == id) : J.Int(args, "index") ?? -1;
            if (J.Str(args, "relative") is string rel) idx = t.Index + (rel == "back" || rel == "prev" ? -1 : 1);
            if (idx < 0 || idx >= t.Steps.Count) throw new ArgumentException($"no such step (have {t.Steps.Count})");
            t.State = TutorialState.Running;
            Enter(idx, "agent");
            return TutorialJson();
        }
    }

    public static JsonObject CompleteStep(JsonNode args)
    {
        lock (Sync)
        {
            var t = RequireOwned();
            if (J.Int(args, "expectIndex") is int e && e != t.Index)
                throw new InvalidOperationException($"current step is {t.Index}, not {e}");
            if (t.State == TutorialState.Running && t.AdvanceAt == null) Complete("agent", immediate: J.Bool(args, "immediate") ?? false);
            return TutorialJson();
        }
    }

    /// <summary>
    /// The agent ends the tutorial. Every step done (a keepOpen tutorial waiting for more): it finishes like any
    /// tutorial, with the end card (done, save, repeat) — the user is told, not left with a card that vanishes.
    /// Midway, or with outro:false: stopped, the card hidden.
    /// </summary>
    public static JsonObject StopTutorial(JsonNode? args = null)
    {
        lock (Sync)
        {
            if (_tut is { Active: true } && (J.Bool(args, "outro") ?? _tut.State == TutorialState.Waiting))
            {
                RequireOwned();
                FinishNow(_tut, "agent");
                _stepVersion++;
                return TutorialJson();
            }
            if (_tut is { Active: true })
            {
                RequireOwned();
                _tut.State = TutorialState.Stopped;
                ApplySettings(Settings.Global);
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
            if (t is { OutroOpen: true } && action is "stop" or "close")
            {
                Outro("outro_close", source);
                return;
            }
            if (t == null || !t.Active) return;
            switch (action)
            {
                case "done":
                case "next":
                    if (t.State == TutorialState.Running && t.AdvanceAt == null) Complete(source);
                    break;
                case "back":
                    Events.Emit("step_back", new() { ["from"] = t.Index, ["to"] = Math.Max(0, t.Index - 1), ["source"] = source });
                    t.State = TutorialState.Running;
                    Enter(Math.Max(0, t.Index - 1), source);
                    break;
                case "skip":
                    if (t.State != TutorialState.Running) break;
                    Events.Emit("step_skipped", new() { ["index"] = t.Index, ["id"] = t.Current?.Id, ["source"] = source });
                    Next();
                    break;
                case "undo":
                    Undo(source);
                    break;
                case "help":
                case "cant_find":
                    RequestHelp("cant_find", source);
                    break;
                case "dont_understand":
                    RequestHelp("dont_understand", source);
                    break;
                case "stop":
                    t.State = TutorialState.Stopped;
                    ApplySettings(Settings.Global);
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
                case "outro_save" or "outro_repeat" or "outro_continue" or "outro_close":
                    Outro(action, source);
                    break;
                case "wait_finish":
                    if (t is { State: TutorialState.Waiting }) { FinishNow(t, "user"); _stepVersion++; }
                    break;
            }
        }
    }

    public static void ApplySettings(Settings s)
    {
        Settings.Current = s;
        Palette.Accent = s.Accent;
    }

    private static Tutorial Require() => _tut is { Active: true } t ? t : throw new InvalidOperationException("no active tutorial");

    private static void Enter(int idx, string? source = null)
    {
        var t = _tut!;
        bool back = t.Index >= 0 && idx < t.Index;
        t.Index = idx;
        t.AdvanceAt = null;
        t.CompletedHow = null;
        var s = t.Steps[idx];
        s.ResetRuntime();
        s.EnteredAt = DateTime.UtcNow;
        // coming back must not bounce straight forward again because the step's goal is already true
        s.Review = back;
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
            t.State = TutorialState.Waiting;
            t.WaitingSince = DateTime.UtcNow;
            Events.Emit("tutorial_waiting", new() { ["title"] = t.Title, ["completed"] = t.Steps.Count });
        }
        else FinishNow(t, null);
        _stepVersion++;
    }

    private static void FinishNow(Tutorial t, string? reason)
    {
        t.State = TutorialState.Finished;
        t.OutroOpen = t.Outro;
        // the end card keeps the tutorial's look until it is closed
        if (!t.OutroOpen) ApplySettings(Settings.Global);
        var ev = new JsonObject { ["title"] = t.Title, ["total"] = t.Steps.Count, ["outro"] = t.Outro, ["savedAs"] = t.SavedAs };
        if (reason != null) ev["reason"] = reason;
        Events.Emit("tutorial_finished", ev);
    }

    /// <summary>
    /// How long the card waits for append_steps with no request at all from the agent. An agent that is still
    /// preparing the next lesson calls tools (screenshot, find_text, validate…), and each call restarts the wait;
    /// one that has gone quiet or away must not leave the user before a spinner forever.
    /// </summary>
    private static readonly TimeSpan WaitForSteps = TimeSpan.FromSeconds(30);

    private static void CheckWaiting(Tutorial t, DateTime now)
    {
        long ticks = System.Threading.Interlocked.Read(ref McpServer.LastRequestTicks);
        var last = ticks == 0 ? t.WaitingSince : new DateTime(Math.Max(ticks, t.WaitingSince.Ticks), DateTimeKind.Utc);
        if (now - last < WaitForSteps) return;
        FinishNow(t, "no_more_steps");
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

    private static void HandleClick(InputEvent c)
    {
        var pt = new PointF(c.X, c.Y);
        var (_, rootPid, childPid) = Native.HitWindow(pt);
        if (rootPid == OwnPid || childPid == OwnPid) return;   // card / buttons handle themselves

        lock (Sync)
        {
            var t = _tut;
            var step = t is { State: TutorialState.Running, AdvanceAt: null } ? t.Current : null;
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
                // a modal dialog of the app is open: the click never reaches the main window
                if (step != null && step.Targets.Contains(best) && BlockingDialog(step) is string dialog && Native.HitWindow(pt).root != GetForegroundWindow())
                {
                    Events.Emit("blocked", new() { ["step"] = t!.Index, ["mark"] = best.Id, ["dialog"] = dialog, ["x"] = c.X, ["y"] = c.Y });
                    return;
                }
                OnHit(best, step, pt, c.Button);
                return;
            }
            if (step != null && step.Targets.Any(m => m.OpensArmedAt != null)) return;
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
                    if (nearest.d <= 5 * nearest.m.Geo.A.Scale && nearest.m.Button != "any" && nearest.m.Button != c.Button)
                    {
                        ev["wrongButton"] = true;
                        ev["expectedButton"] = nearest.m.Button;
                    }
                }
                Events.Emit("miss", ev);
                step.Misses.Add((JsonObject)ev.DeepClone());
                if (step.Misses.Count > 8) step.Misses.RemoveAt(0);
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
        if (m.Opens != null && !m.Satisfied)
        {
            // the click only counts once the window / pane it should open actually shows up
            kind = "hit";
            m.OpensArmedAt = DateTime.UtcNow;
            m.OpensBefore = Resolver.MatchingWindows(m.Opens);
            m.OpensPresentBefore = Resolver.Get(m.Opens.Key)?.Found == true;
        }
        else if (m.Check != null) kind = "hit";
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
        if (m.OpensArmedAt != null) ev["waitingFor"] = m.Opens!.Describe();
        Events.Emit(kind, ev);
    }

    // =========================================================== per-frame (UI thread)

    public sealed class Frame
    {
        public Scene? Scene;
        public bool Changed;
        public List<ButtonGeo> Buttons = new();
        public CardModel Card = new();
        public MascotInput? Mascot;
    }

    public static Frame Tick()
    {
        var now = DateTime.UtcNow;
        var f = new Frame();
        lock (Sync)
        {
            var t = _tut;
            var step = t is { State: TutorialState.Running } ? t.Current : null;
            var marks = (step?.Targets ?? Enumerable.Empty<Mark>()).Concat(Free).ToList();

            // what the resolver should keep fresh
            var anchors = marks.Select(m => m.Anchor)
                .Concat(marks.Where(m => m.Opens != null).Select(m => m.Opens!))
                .Concat(marks.Where(m => m.Check?.Anchor != null).Select(m => m.Check!.Anchor!))
                .Concat(step?.Until.Select(c => c.Anchor ?? c.Pixel?.Anchor).OfType<AnchorSpec>() ?? Enumerable.Empty<AnchorSpec>())
                // the tutorial's main window stays resolved so the card can pick the right monitor
                .Concat(t is { Active: true } && t.Defaults.Process != null ? new[] { t.Defaults } : Array.Empty<AnchorSpec>())
                .Concat(InputAnchors(step));
            var anchorList = anchors.ToList();
            Finder.SetWanted(anchorList);
            Resolver.SetWanted(anchorList.Select(a => a.Find != null ? a.WithoutFind() : a),
                t is { Active: true } && t.Defaults.Process != null ? new[] { t.Defaults.Process } : Array.Empty<string>());

            InputHook.KeysWanted = WantsKeys(step);
            InputHook.MotionWanted = WantsMotion(step);
            var hold = HoldKeys(step);
            if (!hold.SequenceEqual(InputHook.HoldWatched)) InputHook.HoldWatched = hold;
            int fgPid = ForegroundPid();
            foreach (var m in marks) ComputeMark(m, step != null && step.Targets.Contains(m) ? t : null, fgPid, now);
            if (step is { Sequence: true }) ApplySequence(step, now);
            WatchMissing(t, step, now);
            var visible = marks.Where(m => m.Geo != null).ToList();
            Declutter(visible);

            bool sampleNow = (now - _lastPixel).TotalMilliseconds >= 150;
            if (sampleNow) _lastPixel = now;
            var probes = new List<(PointF p, int r)>();
            var due = new List<(Mark m, PointF pt)>();
            foreach (var m in visible.Where(m => m.Check != null))
            {
                if (ProbePoint(m) is not { } pp) continue;
                probes.Add((pp.pt, m.Check!.Radius));
                // only after the canvas has shown this mark (with its probe hole) — otherwise the probe can
                // still see whatever we drew there a moment ago
                bool rendered = m.ShownAt is DateTime sa && (now - sa).TotalMilliseconds > 120;
                if (sampleNow && rendered && CanSample(m, pp.pt, pp.ag)) due.Add((m, pp.pt));
            }
            if (due.Count > 0) RunPixelChecks(due);

            // compose the canvas
            var sc = new Scene { Pulse = Animate ? 0.5f + 0.5f * (float)Math.Sin(now.Ticks / (double)TimeSpan.TicksPerMillisecond / 1600.0 * Math.PI * 2) : 1f };
            float dim = 0;
            // with the mascot on, a step's highlight appears when it has arrived (appeared, or stopped walking)
            bool holdTargets = step != null && HoldForMascot(t!, step, now);
            foreach (var m in visible)
            {
                var g = m.Geo!;
                if (holdTargets && !m.Satisfied && step!.Targets.Contains(m))
                {
                    m.ShownAt = null;
                    f.Card.Avoid.Add(g.Bounds);
                    continue;
                }
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
            if (step != null) AddDragVisuals(step, sc, f.Card.Avoid, fgPid);
            if (step != null && t!.AdvanceAt == null) AddMotionVisuals(step, sc, f.Card.Avoid, fgPid);
            if (step != null && t!.AdvanceAt == null) AddDialogVisuals(step, sc, f.Card.Avoid);
            CheckOpens(marks, step, now);
            AddPickVisuals(sc, f.Card.Avoid);
            if (step?.Sketch is { } sketch) AddSketchVisuals(sketch, sc, f.Card.Avoid);
            // a dialog the app waits on comes first: scrolling the pane behind it does nothing until it is answered
            if (step == null || BlockingDialogWindow(step) == null) AddScrollVisuals(marks, sc, f.Card.Avoid, fgPid);
            if (step?.HighlightUntil is DateTime hu)
            {
                if (now < hu)
                {
                    dim = Math.Max(dim, 0.55f);
                    if (now >= step.NextPing)
                    {
                        step.NextPing = now.AddMilliseconds(700);
                        foreach (var m in step.Targets.Where(m => m.Geo != null && !m.Satisfied))
                        {
                            var h0 = m.Geo!.Hits.FirstOrDefault();
                            var c0 = h0 == null ? m.Geo.Center : h0.Circle ? h0.C : new PointF(h0.Rect.X + h0.Rect.Width / 2, h0.Rect.Y + h0.Rect.Height / 2);
                            float r0 = h0 == null ? 20 * m.Geo.A.Scale : h0.Circle ? h0.R : Math.Max(h0.Rect.Width, h0.Rect.Height) / 2;
                            Ripples.Add(new Ripple { C = c0, R0 = r0, Color = Palette.Accent, T0 = now, Scale = m.Geo.A.Scale * 2.2f });
                        }
                    }
                }
                else step.HighlightUntil = null;
            }
            sc.Dim = dim;
            sc.Probes.AddRange(probes);
            if (step != null) sc.Probes.AddRange(ConditionProbes(step));
            if (_pick != null && Native.GetCursorPos(out var cur)) sc.Probes.Add((new PointF(cur.X, cur.Y), 1));
            if (dim > 0) sc.Pulse = 1f;    // keep full-screen frames static
            Ripples.RemoveAll(r => (now - r.T0).TotalMilliseconds > Ripple.DurationMs);
            sc.Ripples.AddRange(Ripples);
            f.Scene = sc;

            if (t is { Active: true }) EvaluateTutorial(t, now, fgPid);
            BuildCard(f.Card, _tut, fgPid);
            f.Mascot = MascotFor(_tut, f.Card);

            var sig = Signature(sc);
            f.Changed = sig != _lastSignature || sc.Ripples.Count > 0;
            _lastSignature = sig;
        }
        return f;
    }

    private static string Signature(Scene sc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append((int)(sc.Dim * 100)).Append('|').Append((int)(sc.Pulse * Renderer.PulseSteps)).Append('|');
        // moving parts (the dot running along a path, the wheel's arrows) need every frame
        if (sc.Prims.Any(x => x.prim.Kind is PrimKind.Path or PrimKind.Wheel)) sb.Append(DateTime.UtcNow.Ticks / (TimeSpan.TicksPerMillisecond * 33)).Append('|');
        foreach (var (p, a) in sc.Prims)
            sb.Append((int)p.Kind).Append(',').Append((int)p.A.X).Append(',').Append((int)p.A.Y).Append(',').Append((int)p.B.X).Append(',').Append((int)p.B.Y)
              .Append(',').Append((int)p.R).Append(',').Append((int)p.Rect.X).Append(',').Append((int)p.Rect.Y).Append(',').Append((int)p.Rect.Width)
              .Append(',').Append((int)p.Rect.Height).Append(',').Append((int)(a * 20)).Append(p.On ? '1' : '0').Append(p.Text).Append(';');
        foreach (var h in sc.Holes) sb.Append((int)h.C.X).Append(',').Append((int)h.Rect.X).Append(';');
        return sb.ToString();
    }

    /// <summary>Screenshot / recording tools: their windows neither take the user "out of the app" nor cover marks.</summary>
    private static readonly HashSet<string> CaptureTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ScreenClippingHost", "SnippingTool", "ScreenSketch", "ShareX", "Greenshot", "Lightshot", "Snagit32", "SnagitEditor",
        "obs64", "obs32", "flameshot", "PicPick", "Monosnap",
    };

    private static int _lastAppFgPid;

    /// <summary>
    /// A titled window of the same app is in front of the window the step's targets live in — typically a modal
    /// dialog ("Pattern 2 name"): clicks on the main window are swallowed by the app until it is closed.
    /// Context menus have no title and sequence targets inside the dialog are fine.
    /// </summary>
    private static string? BlockingDialog(Step s) => BlockingDialogWindow(s)?.title;

    // the card, the dialog frame and help all ask within one frame: work it out once per frame (and per step)
    private static (Step step, long tick, (IntPtr hwnd, string title, Rectangle frame)? value)? _dialogMemo;

    private static (IntPtr hwnd, string title, Rectangle frame)? BlockingDialogWindow(Step s)
    {
        long tick = Environment.TickCount64;
        if (_dialogMemo is { } dm && ReferenceEquals(dm.step, s) && tick - dm.tick < 25) return dm.value;
        var v = ComputeBlockingDialog(s);
        _dialogMemo = (s, tick, v);
        return v;
    }

    private static IntPtr _lastAppFgWindow;

    /// <summary>
    /// The foreground window, except that a screenshot tool in front does not count: while the user snips the
    /// screen, the app's window that was in front before (a dialog, say) still is as far as the step is concerned.
    /// </summary>
    private static IntPtr AppForegroundWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return fg;
        int pid = Native.ProcessId(fg);
        if (pid != 0 && CaptureTools.Contains(ProcName(pid)))
            return _lastAppFgWindow != IntPtr.Zero && Native.IsWindowVisible(_lastAppFgWindow) ? _lastAppFgWindow : fg;
        _lastAppFgWindow = fg;
        return fg;
    }

    private static (IntPtr hwnd, string title, Rectangle frame)? ComputeBlockingDialog(Step s)
    {
        var fg = AppForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var roots = s.Targets.Where(m => m.Geo != null && !m.Satisfied).Select(m => m.Geo!.A.Root).Where(r => r != IntPtr.Zero).ToHashSet();
        int[] pids = s.Targets.Where(m => m.Geo != null).SelectMany(m => m.Geo!.A.Pids).ToArray();
        if (roots.Count == 0 && _tut?.Defaults is { Process: not null } d && AnchorNow(d, out _) is AnchorGeo main0)
        {
            // a step without visible targets (e.g. "press F4"): compare against the app's main window
            roots.Add(main0.Root);
            pids = main0.Pids;
        }
        if (roots.Count == 0 || roots.Contains(fg)) return null;
        int pid = Native.ProcessId(fg);
        if (!pids.Contains(pid)) return null;
        // a sequence step that points into this very window is not "blocked" by it
        if (s.Targets.Any(m => !m.Satisfied && m.Geo?.A.Root == fg)) return null;
        var title = CleanTitle(Native.WindowTitle(fg));
        if (title.Length == 0) return null;
        var frame = Native.FrameRect(fg);
        var main = Native.FrameRect(roots.First());
        if ((long)frame.Width * frame.Height >= (long)main.Width * main.Height * 0.6) return null;   // another big window, not a dialog
        return (fg, title, frame);
    }

    /// <summary>Window titles may carry control or invisible characters (FL Studio's do): spaces instead, once.</summary>
    private static string CleanTitle(string title)
    {
        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var ch in title)
        {
            var cat = char.GetUnicodeCategory(ch);
            bool junk = char.IsControl(ch) || cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.PrivateUse
                        or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.OtherNotAssigned;
            char c = junk || char.IsWhiteSpace(ch) ? ' ' : ch;
            if (c == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Frames the app's blocking dialog so the user sees what has to be answered first.</summary>
    private static void AddDialogVisuals(Step s, Scene sc, List<RectangleF> avoid)
    {
        if (BlockingDialogWindow(s) is not { } d) return;
        var center = new PointF(d.frame.X + d.frame.Width / 2f, d.frame.Y + d.frame.Height / 2f);
        float k = Native.ScaleAt(center);
        var r = (RectangleF)d.frame;
        r.Inflate(6 * k, 6 * k);
        sc.Prims.Add((new Prim { Kind = PrimKind.Rect, Rect = r, Color = Palette.Warn, Width = 3.5f * k, Radius = 8 * k, Scale = k }, 1f));
        avoid.Add(r);
        // the mascot stands by the dialog and says this itself; a caption of its own only when there is no mascot
        if (_tut is { Mascot: true } && Settings.Current.Mascot) return;
        var text = Settings.Current.Text.DialogMark;
        float px = 15 * k;
        var sz = Theme.Measure(text, Theme.Font(px), 360 * k);
        float w = sz.Width + 24 * k + 4 * k, h = sz.Height + 16 * k;
        var mon = Native.MonitorRectAt(center);
        float x = Math.Clamp(center.X - w / 2, mon.Left + 4 * k, mon.Right - w - 4 * k);
        float y = r.Top - 10 * k - h;
        if (y < mon.Top + 4 * k) y = r.Bottom + 10 * k;
        var box = new RectangleF(x, y, w, h);
        sc.Prims.Add((new Prim { Kind = PrimKind.Label, Rect = box, Text = text, Size = px, Color = Palette.Warn, Scale = k, Radius = 8 * k }, 1f));
        avoid.Add(box);
    }

    private static int ForegroundPid()
    {
        var fg = GetForegroundWindow();
        int pid = fg == IntPtr.Zero ? 0 : Native.ProcessId(fg);
        if (pid != 0 && CaptureTools.Contains(ProcName(pid))) return _lastAppFgPid;
        _lastAppFgPid = pid;
        return pid;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static string ProcName(int pid)
    {
        long now = Environment.TickCount64;
        lock (ProcNames)
        {
            if (ProcNames.TryGetValue(pid, out var c) && now - c.at < ProcNameTtlMs) return c.name;
            string n;
            try { using var p = Process.GetProcessById(pid); n = p.ProcessName; } catch { n = ""; }
            if (ProcNames.Count > 512)
                foreach (var k in ProcNames.Where(kv => now - kv.Value.at >= ProcNameTtlMs).Select(kv => kv.Key).ToList()) ProcNames.Remove(k);
            ProcNames[pid] = (n, now);
            return n;
        }
    }

    /// <summary>Is the user currently working in this app? (foreground window belongs to it)</summary>
    /// <summary>Windows' own UI the user passes through: Start menu, search, notification centre, quick settings.</summary>
    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "ShellHost",
    };

    private static readonly HashSet<string> ShellSurfaces = new(StringComparer.OrdinalIgnoreCase) { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>The taskbar: part of the desktop, always visible, never "the app in front".</summary>
    private static bool OnShellSurface(AnchorSpec a, IntPtr root) =>
        (a.WindowClass != null && ShellSurfaces.Contains(a.WindowClass)) || (root != IntPtr.Zero && ShellSurfaces.Contains(Native.ClassName(root)));

    private static bool AppActive(int[] pids, string? process, int fgPid)
    {
        if (fgPid == 0) return false;
        if (pids.Contains(fgPid)) return true;
        return process != null && ProcName(fgPid).Equals(process, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is the user in one of the apps this step is about? (the tutorial's app, any target's app, the window a
    /// click should open, condition anchors). A step whose click opens another program keeps running there.
    /// </summary>
    private static bool StepActive(Tutorial t, Step s, int fgPid)
    {
        var procs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (t.Defaults.Process != null) procs.Add(t.Defaults.Process);
        foreach (var m in s.Targets)
        {
            // the taskbar is always on screen: a step pointing at it is "on" whatever app is in front
            if (!m.Satisfied && OnShellSurface(m.Anchor, m.Geo?.A.Root ?? IntPtr.Zero)) return true;
            if (m.Anchor.Process != null) procs.Add(m.Anchor.Process);
            if (m.Opens != null)
            {
                if (m.Opens.Process == null && m.OpensArmedAt != null) return true;   // "any process" while waiting
                if (m.Opens.Process != null) procs.Add(m.Opens.Process);
            }
        }
        foreach (var c in s.Until)
            foreach (var a in new[] { c.Anchor, c.Pixel?.Anchor, c.From?.Anchor, c.To?.Anchor })
                if (a?.Process != null) procs.Add(a.Process);
        if (procs.Count == 0) return true;
        if (fgPid == 0) return false;
        var fg = ProcName(fgPid);
        // the Start menu, search or the notification centre is in front: the user is on the way to (or back from)
        // the program the step is about — launching it from Start is part of the tutorial, not leaving it
        return procs.Contains(fg) || ShellHosts.Contains(fg);
    }

    private const int OpensWindowsMaxAgeMs = 300;

    /// <summary>Armed "opens" targets: satisfied by a NEW matching window, or a pane that was absent at click time.</summary>
    private static void CheckOpens(List<Mark> marks, Step? step, DateTime now)
    {
        foreach (var m in marks.Where(m => m.OpensArmedAt != null))
        {
            var a = m.Opens!;
            bool opened = false;
            string? what = null;
            if (a.Path.Count > 0 || a.Title != null)
            {
                var r = Resolver.Get(a.Key);
                if (r is { Found: true } && !m.OpensPresentBefore) { opened = true; what = Native.WindowTitle(r.Root); }
            }
            if (!opened && a.Path.Count == 0)
            {
                // the resolver's window list (a few frames old at most) instead of walking every window each frame
                var fresh = Resolver.MatchingWindows(a, Resolver.RecentWindows(OpensWindowsMaxAgeMs)).Where(h => m.OpensBefore == null || !m.OpensBefore.Contains(h)).ToList();
                if (fresh.Count > 0) { opened = true; what = Native.WindowTitle(fresh[0]); }
            }
            if (opened)
            {
                m.OpensArmedAt = null;
                m.Dismissed = true;
                if (step != null && step.Targets.Contains(m)) step.History.Push((m, false));
                Events.Emit("opened", new() { ["mark"] = m.Id, ["name"] = m.Label, ["window"] = what, ["expected"] = a.Describe(), ["step"] = _tut?.Index });
            }
            else if ((now - m.OpensArmedAt!.Value).TotalMilliseconds > m.OpensTimeoutMs)
            {
                m.OpensArmedAt = null;
                Events.Emit("no_effect", new() { ["mark"] = m.Id, ["name"] = m.Label, ["expected"] = a.Describe(), ["step"] = _tut?.Index, ["waitedMs"] = m.OpensTimeoutMs });
            }
        }
    }

    public static AnchorGeo? AnchorNow(AnchorSpec a, out string reason)
    {
        reason = "ok";
        if (a.IsScreen)
        {
            var p = new PointF(a.Dx, a.Dy);
            return new AnchorGeo { P = p, Scale = Native.ScaleAt(p) };
        }
        var r = Resolver.Get(a.Find != null ? a.WithoutFind().Key : a.Key);
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
        var geo = new AnchorGeo
        {
            Bounds = bounds, P = new PointF(o.X + a.Dx, o.Y + a.Dy), Root = root, Pids = r.Pids,
            Scale = Native.ScaleAt(new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)),
        };
        if (a.Path.Count > 0 && a.HasPoint) geo.View = bounds;   // a point inside a pane: it can scroll out of view
        if (a.Find != null)
        {
            var fr = Finder.Get(a.Key);
            if (fr == null) { reason = "searching"; return null; }
            // found in one of the app's menus / dialogs (an anchor naming only the app): the mark belongs to that window
            if (fr.Root != IntPtr.Zero && fr.Root != root)
            {
                if (!Native.IsWindow(fr.Root) || !Native.IsWindowVisible(fr.Root)) { reason = "closed"; return null; }
                root = fr.Root;
                frame = Native.FrameRect(root);
                bounds = frame;
                o = a.OriginOf(bounds);
                geo.Root = root;
                geo.Bounds = bounds;
            }
            PointF? seen = fr.Found ? fr.Rel : fr.LastRel;
            if (seen is not PointF m) { reason = a.Find.Text != null ? "text not visible" : "template not visible"; return null; }
            var hit = new PointF(frame.X + m.X, frame.Y + m.Y);
            // the found piece itself (hit is its centre): what a shape without coordinates frames
            if (fr.Size.Width > 0 && fr.Size.Height > 0)
                geo.Found = new RectangleF(hit.X - fr.Size.Width / 2, hit.Y - fr.Size.Height / 2, fr.Size.Width, fr.Size.Height);
            float px = a.Find.Axis == "y" ? o.X : hit.X, py = a.Find.Axis == "x" ? o.Y : hit.Y;
            geo.P = new PointF(px + a.Dx, py + a.Dy);
            // the area searched is the area it can scroll out of
            geo.View = a.Find.Within is RectangleF fw ? new RectangleF(bounds.X + fw.X, bounds.Y + fw.Y, fw.Width, fw.Height) : bounds;
            geo.FromLastKnown = !fr.Found;
            if (!fr.Found && fr.LastRel is PointF lr)
            {
                double gone = (DateTime.UtcNow - fr.LastSeenAt).TotalSeconds;
                if (fr.Moving)
                {
                    // lost while scrolling: a whole-area read is under way. Show nothing (rather than a guess)
                    // until it has had its chance; after that it really is off, the way it was moving
                    if (gone < 0.9) { reason = "searching"; return null; }
                    // a pinned copy (sticky header) that went: the way the content scrolled; else the way it moved
                    geo.GoneDir = fr.Went ?? GoneDirection(lr, fr.PrevRel, frame, geo.View.Value, true);
                }
                else
                {
                    geo.GoneDir = GoneDirection(lr, fr.PrevRel, frame, geo.View.Value, false);
                    // it was still and is now unreadable (selected, hovered, redrawn): not scrolled away, so
                    // keep the mark where it was for a while
                    if (geo.GoneDir == null && gone < 3) geo.FromLastKnown = false;
                }
            }
        }
        return geo;
    }

    private static void ComputeMark(Mark m, Tutorial? t, int fgPid, DateTime now)
    {
        var ag = AnchorNow(m.Anchor, out var reason);
        if (ag == null)
        {
            m.Geo = null;
            m.Visibility = reason;
            // a text/template not seen yet: which way is unknown, but where to scroll is — the area it is looked for in.
            // Only inside a named element or part (a list, a panel): a text looked for in a whole app is usually in a
            // menu or dialog not open yet, and a wheel in the window's corner would send the user the wrong way
            m.ScrollHint = reason is "text not visible" or "template not visible" && ScrollsToFind(m.Anchor) && FindArea(m.Anchor) is RectangleF fa
                ? ("any", fa, new PointF(fa.X + fa.Width / 2, fa.Y + fa.Height / 2), -1) : null;
            return;
        }
        if (!m.Anchor.IsScreen && ag.Pids.Length > 0 && Native.IsAboveUs(Native.ProcessId(ag.Root)))
        {
            // the app runs as administrator and we do not: its clicks never reach our hooks
            m.Geo = null;
            m.Visibility = "elevated";
            m.ScrollHint = null;
            return;
        }
        if (!m.Anchor.IsScreen)
        {
            bool hideInactive = m.HideInactive ?? t?.HideWhenInactive ?? HideWhenInactiveDefault;
            if (hideInactive && !OnShellSurface(m.Anchor, ag.Root) && !AppActive(ag.Pids, m.Anchor.Process, fgPid)) { m.Geo = null; m.Visibility = "inactive"; return; }
        }
        var geo = Layout.Build(m, ag, m.Satisfied);
        m.ScrollHint = null;
        // Out of view only if the point clicked (the anchor point) is out too: a frame drawn to one side of its point
        // (a bar in the playlist, from the point to the right) can reach past the pane's edge while the point itself,
        // where the user clicks, is in plain view. Judging by the frame alone hid such a target behind a scroll hint
        // and its clicks went uncounted.
        if (ag.View is RectangleF view && OutsideView(geo.Center, view, ag.Scale, ag.FromLastKnown) is string seenDir
            && (ag.FromLastKnown || OutsideView(ag.P, view, ag.Scale, false) != null)
            && (seenDir != "unknown" || ag.GoneDir == null ? seenDir : ag.GoneDir) is string dir0)
        {
            string dir = dir0 == "unknown" ? "any" : dir0;      // not seen and no idea which way: just "scroll here"
            // the target scrolled out of the pane: point at the edge it went past
            var toward = new PointF(Math.Clamp(geo.Center.X, view.Left + 40 * ag.Scale, view.Right - 40 * ag.Scale),
                                    Math.Clamp(geo.Center.Y, view.Top + 40 * ag.Scale, view.Bottom - 40 * ag.Scale));
            // how far past the edge it is, when we know where it is (UIA rect, or a text/template seen before)
            float beyond = dir switch
            {
                "down" => geo.Center.Y - view.Bottom, "up" => view.Top - geo.Center.Y,
                "right" => geo.Center.X - view.Right, "left" => view.Left - geo.Center.X,
                _ => -1,
            };
            // a text/template out of view is only known by where it was last seen — no distance from that
            m.ScrollHint = (dir, view, toward, ag.FromLastKnown ? -1 : Math.Max(0, beyond));
            m.Geo = null;
            m.Visibility = "scroll-" + dir0;
            return;
        }
        if (!m.Anchor.IsScreen)
        {
            if (!OnAnyMonitor(geo.Center)) { m.Geo = null; m.Visibility = "offscreen"; return; }
            if (!ShowsAnchorWindow(geo.Center, ag)) { m.Geo = null; m.Visibility = "occluded"; return; }
        }
        if (m.AutoColor == null && Settings.Current.AutoColor && !m.Anchor.IsScreen)
        {
            // one colour per step, so its targets don't look like different kinds of things
            var st = t?.Current;
            m.AutoColor = st?.AutoColor ?? PickContrast(geo);
            if (st != null) st.AutoColor ??= m.AutoColor;
            geo = Layout.Build(m, ag, m.Satisfied);
        }
        m.Geo = geo;
        m.Visibility = "visible";
    }

    /// <summary>Samples the app around the target (a ring just outside it, plus its centre) once per mark.</summary>
    private static Color PickContrast(MarkGeo geo)
    {
        var tb = geo.TargetBounds;
        var c = new PointF(tb.X + tb.Width / 2, tb.Y + tb.Height / 2);
        float rx = tb.Width / 2 + 10 * geo.A.Scale, ry = tb.Height / 2 + 10 * geo.A.Scale;
        var points = new List<PointF> { c };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            var p = new PointF(c.X + (float)Math.Cos(a) * rx, c.Y + (float)Math.Sin(a) * ry);
            if (Native.HitWindow(p).rootPid == OwnPid) continue;     // our card is not the app
            points.Add(p);
        }
        // one screen read for all nine points
        return Contrast.Pick(ScreenCapture.SampleMany(points, 1));
    }

    /// <summary>
    /// A text/template that is no longer on screen: which edge did it go past? Its last two sightings tell which
    /// way it was moving; failing that, where it was last seen (top third → up, bottom third → down).
    /// </summary>
    private static string? GoneDirection(PointF last, PointF? before, Rectangle frame, RectangleF view, bool moving)
    {
        float ny = (frame.Y + last.Y - view.Top) / Math.Max(1, view.Height);
        if (moving)
        {
            // it was scrolling and a whole-area read did not find it: it went the way it was moving
            if (before is PointF b)
            {
                float dx = last.X - b.X, dy = last.Y - b.Y;
                if (Math.Abs(dy) >= 2 && Math.Abs(dy) >= Math.Abs(dx)) return dy < 0 ? "up" : "down";
                if (Math.Abs(dx) > 20) return dx < 0 ? "left" : "right";
            }
            return ny < 0.5f ? "up" : "down";
        }
        // it was still: only a text right at the top or bottom edge can have gone out unnoticed. Never sideways
        // by position: lines start at the left of a pane, and an unreadable moment there is not a scroll
        if (ny < 0.15f) return "up";
        if (ny > 0.85f) return "down";
        return null;
    }

    private static string? OutsideView(PointF c, RectangleF view, float s, bool lastKnown)
    {
        float m = 6 * s;
        if (c.Y < view.Top + m) return "up";
        if (c.Y > view.Bottom - m) return "down";
        if (c.X < view.Left + m) return "left";
        if (c.X > view.Right - m) return "right";
        return lastKnown ? "unknown" : null;     // anchor not seen right now but its last place is inside: just ask to scroll
    }

    /// <summary>An arrow at the pane's edge plus a caption, for targets that scrolled out of view.</summary>
    /// <summary>
    /// The target scrolled out of its pane: a scroll wheel at the edge it went past, arrows running that way, a
    /// caption, and "≈ N notches" when we know how far it is (~60 px a notch at 100 %). Scrolled past it, the
    /// target is beyond the other edge and the hint turns round by itself. Nothing here is reported to the agent.
    /// </summary>
    private static void AddScrollVisuals(List<Mark> marks, Scene sc, List<RectangleF> avoid, int fgPid)
    {
        var shown = new HashSet<string>();
        foreach (var m in marks)
        {
            if (m.ScrollHint is not { } h || m.Satisfied) continue;
            if (h.dir is not ("up" or "down" or "left" or "right" or "any")) continue;
            // like the marks: nothing over another app the user switched to
            if (!m.Anchor.IsScreen && (m.HideInactive ?? _tut?.HideWhenInactive ?? HideWhenInactiveDefault)
                && !OnShellSurface(m.Anchor, m.Geo?.A.Root ?? IntPtr.Zero) && !AppActive(m.Geo?.A.Pids ?? Array.Empty<int>(), m.Anchor.Process, fgPid)) continue;
            if (!shown.Add(h.dir + h.view)) continue;        // one hint per pane and direction
            float s = Native.ScaleAt(h.toward);
            var at = ScrollHintPoint(h.dir, h.view, h.toward, s);
            var (text, notches) = ScrollHintText(h.dir, h.beyond, h.view, s);
            var wheel = new Prim { Kind = PrimKind.Wheel, A = at, Rect = Prim.WheelBox(at, s),
                                   Text = h.dir + "|" + (notches > 0 ? "≈" + notches : ""), Color = Palette.Accent, Scale = s };
            float px = 14 * s;
            var sz = Theme.Measure(text, Theme.Font(px), 300 * s);
            float w = sz.Width + 28 * s, hh = sz.Height + 16 * s;
            // the caption on the inner side of the wheel
            var box = h.dir switch
            {
                "any" => new RectangleF(at.X - 30 * s - w, at.Y - hh / 2, w, hh),
                "down" => new RectangleF(at.X - w / 2, at.Y - 44 * s - hh, w, hh),
                "up" => new RectangleF(at.X - w / 2, at.Y + 44 * s, w, hh),
                "right" => new RectangleF(at.X - 30 * s - w, at.Y - hh / 2, w, hh),
                _ => new RectangleF(at.X + 30 * s, at.Y - hh / 2, w, hh),
            };
            var label = new Prim { Kind = PrimKind.Label, Rect = box, Text = text, Size = px, Color = Palette.Accent, Scale = s, Radius = 8 * s };
            sc.Prims.Add((wheel, 1f));
            avoid.Add(wheel.Rect);
            // the mascot says it in its bubble: the caption would only repeat it
            if (_tut is { Mascot: true } && Settings.Current.Mascot) continue;
            sc.Prims.Add((label, 1f));
            avoid.Add(box);
        }
    }

    /// <summary>The area a find anchor is looked for in (the element, or its "within" part), on screen.</summary>
    /// <summary>A find anchor whose search area is a scrollable part (an element or a part of it), not a whole window.</summary>
    internal static bool ScrollsToFind(AnchorSpec a) => a.Find != null && (a.Path.Count > 0 || a.Find.Within != null);

    private static RectangleF? FindArea(AnchorSpec a)
    {
        if (a.Find == null) return null;
        var r = Resolver.Get(a.WithoutFind().Key);
        if (r is not { Found: true } || !Native.IsWindow(r.Root) || Native.IsIconic(r.Root)) return null;
        var frame = Native.FrameRect(r.Root);
        RectangleF b = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height) : frame;
        var area = a.Find.Within is RectangleF w ? new RectangleF(b.X + w.X, b.Y + w.Y, w.Width, w.Height) : b;
        area.Intersect(b);
        return area.Width < 40 || area.Height < 40 ? null : area;
    }

    /// <summary>Where the wheel stands: just inside the edge the target went past, in line with the target.</summary>
    private static PointF ScrollHintPoint(string dir, RectangleF view, PointF toward, float s) => dir switch
    {
        "any" => new PointF(view.Right - 60 * s, view.Y + view.Height / 2),
        "down" => new PointF(toward.X, view.Bottom - 46 * s),
        "up" => new PointF(toward.X, view.Top + 46 * s),
        "right" => new PointF(view.Right - 30 * s, toward.Y),
        _ => new PointF(view.Left + 30 * s, toward.Y),
    };

    private static (string text, int notches) ScrollHintText(string dir, float beyond, RectangleF view, float s)
    {
        var loc = Settings.Current.Text;
        if (dir == "any") return (loc.ScrollFind, 0);
        string text = dir switch { "up" => loc.ScrollUp, "down" => loc.ScrollDown, "left" => loc.ScrollLeft, _ => loc.ScrollRight };
        // bring it in a little past the edge, not just to it
        int notches = beyond < 0 ? 0 : Math.Clamp((int)Math.Ceiling((beyond + Math.Min(view.Height, view.Width) * 0.15f) / (60 * s)), 1, 99);
        return (notches > 0 ? $"{text} · ≈{notches}" : text, notches);
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
        if (CaptureTools.Contains(ProcName(rootPid))) return true;
        if (ag.Pids.Contains(rootPid) || ag.Pids.Contains(childPid))
        {
            var cls = Native.ClassName(root);
            if (cls.Contains("tooltip", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static (PointF pt, AnchorGeo ag)? ProbePoint(Mark m)
    {
        var chk = m.Check!;
        AnchorGeo? ag = chk.Anchor == null ? m.Geo!.A : AnchorNow(chk.Anchor, out _);
        if (ag == null) return null;
        var pt = chk.At is PointF at ? new PointF(ag.P.X + at.X, ag.P.Y + at.Y) : m.Geo!.Center;
        return (pt, ag);
    }

    private static IEnumerable<(PointF p, int r)> ConditionProbes(Step s)
    {
        foreach (var c in s.Until.Where(c => c.Type == "pixel" && c.Pixel?.Anchor != null))
        {
            var ag = AnchorNow(c.Pixel!.Anchor!, out _);
            if (ag == null) continue;
            yield return (c.Pixel.At is PointF pa ? new PointF(ag.P.X + pa.X, ag.P.Y + pa.Y) : ag.P, c.Pixel.Radius);
        }
    }

    /// <summary>Whether the mark's probe can be read now: not already being read, and the app itself is under it.</summary>
    private static bool CanSample(Mark m, PointF pt, AnchorGeo ag)
    {
        if (m.CheckSampling) return false;
        if (!m.Anchor.IsScreen && !ShowsAnchorWindow(pt, ag)) return false;   // covered: don't conclude anything
        return Native.HitWindow(pt).rootPid != OwnPid;  // under our card / a button: can't see the app there
    }

    /// <summary>
    /// Reads the screen at the marks' probes off the UI thread and outside <see cref="Sync"/> (a screen read waits
    /// for the compositor; clicks and tool calls must not wait for it), then judges the colours under the lock.
    /// All the probes due in one frame share one screen read: a step with a row of 18 cells used to read the screen
    /// 18 times every 150 ms, and those reads, each synchronised with the compositor, made the mouse, the mascot and
    /// the clicks lag.
    /// </summary>
    private static void RunPixelChecks(List<(Mark m, PointF pt)> due)
    {
        var batch = due.Select(d => (d.m, d.pt, chk: d.m.Check!, epoch: d.m.Epoch)).ToList();
        foreach (var b in batch) b.m.CheckSampling = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            var colors = new Color[batch.Count];
            try
            {
                foreach (var grp in Enumerable.Range(0, batch.Count).GroupBy(i => batch[i].chk.Radius))
                {
                    var idx = grp.ToList();
                    var read = ScreenCapture.SampleMany(idx.Select(i => batch[i].pt).ToList(), grp.Key);
                    for (int k = 0; k < idx.Count; k++) colors[idx[k]] = read[k];
                }
            }
            catch (Exception e)
            {
                Log.Debug("pixel checks: " + e.Message);
                lock (Sync) foreach (var b in batch) if (b.m.Epoch == b.epoch) b.m.CheckSampling = false;
                return;
            }
            lock (Sync)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    var (m, pt, chk, epoch) = batch[i];
                    // the step was left or entered again meanwhile: this reading belongs to the old visit
                    if (m.Epoch != epoch) continue;
                    m.CheckSampling = false;
                    if (!ReferenceEquals(m.Check, chk)) continue;
                    var color = colors[i];
                    bool ok = chk.Evaluate(color);
                    if (m.CheckOk == ok) continue;
                    bool first = m.CheckOk == null;
                    m.CheckOk = ok;
                    Events.Emit("check_changed", new()
                    {
                        ["mark"] = m.Id, ["ok"] = ok, ["initial"] = first, ["color"] = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
                        ["at"] = J.PtJson(pt), ["step"] = _tut?.Index,
                    });
                }
            }
        });
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
                else if (!(p.Auto && (p.Kind is PrimKind.Label or PrimKind.Info or PrimKind.Badge))) obstacles.Add(p.Tight());
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
            // step numbers: a small square that must not cover any target, label or other number
            foreach (var p in g.Prims.Where(p => p.Auto && p.Kind == PrimKind.Badge))
            {
                var box = new RectangleF(p.A.X - p.R, p.A.Y - p.R, 2 * p.R, 2 * p.R);
                box = PlaceBadge(box, g.TargetBounds, obstacles, segments, g.A.Scale);
                p.A = new PointF(box.X + box.Width / 2, box.Y + box.Height / 2);
                obstacles.Add(box);
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

    /// <summary>Badges hug their target: try the four outside corners, then the sides, then fall back to Place.</summary>
    private static RectangleF PlaceBadge(RectangleF box, RectangleF t, List<RectangleF> obstacles, List<(PointF a, PointF b, float w)> segments, float s)
    {
        float w = box.Width, h = box.Height, g = 5 * s;   // just past the target's tight outline
        // straight above / below first: in a row of neighbouring cells that gives a tidy zig-zag
        var cands = new[]
        {
            new PointF(t.Left + t.Width / 2 - w / 2, t.Top - h - g), new PointF(t.Left + t.Width / 2 - w / 2, t.Bottom + g),
            new PointF(t.Left - w - g, t.Top - h * 0.5f), new PointF(t.Right + g, t.Top - h * 0.5f),
            new PointF(t.Left - w - g, t.Bottom - h * 0.5f), new PointF(t.Right + g, t.Bottom - h * 0.5f),
            new PointF(t.Left - w - g, t.Top + t.Height / 2 - h / 2), new PointF(t.Right + g, t.Top + t.Height / 2 - h / 2),
        };
        bool Free(RectangleF r) => !obstacles.Any(o => o.Width > 0 && o.IntersectsWith(r))
                                   && !segments.Any(sg => SegmentHits(sg.a, sg.b, Inflate(r, sg.w)));
        if (Free(box)) return box;
        foreach (var c in cands)
        {
            var r = new RectangleF(c, box.Size);
            if (Free(r)) return r;
        }
        return Place(box, t, obstacles, segments, s);
    }

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
        if (t.State == TutorialState.Waiting) { CheckWaiting(t, now); return; }
        if (t.State != TutorialState.Running) return;
        var s = t.Current;
        if (s == null) return;
        if (t.AdvanceAt is DateTime at)
        {
            if (now >= at) Next();
            return;
        }
        // frozen while the user is elsewhere: a minimised / backgrounded app must not look "closed"
        if (!StepActive(t, s, fgPid)) return;
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
                case "uia":
                    EvalUia(c);
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
                    if (!ShowsAnchorWindow(pt, ag) || c.PixelSampling) break;
                    // read off the UI thread and outside the lock; the verdict counts from the next evaluation
                    c.PixelSampling = true;
                    var (px, cond, step, entered) = (c.Pixel, c, s, s.EnteredAt);
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        var color = ScreenCapture.Sample(pt, px.Radius);
                        lock (Sync)
                        {
                            cond.PixelSampling = false;
                            if (!ReferenceEquals(_tut?.Current, step) || step.EnteredAt != entered) return;   // another visit
                            cond.Met = px.Evaluate(color);
                        }
                    });
                    break;
                }
            }
        }
        foreach (var c in s.Until) if (!c.Met) c.SeenUnmet = true;
        var active = s.Until.Where(c => c.Type != "manual" && (!s.Review || c.SeenUnmet)).ToList();
        if (active.Count == 0) return;
        var met = active.FirstOrDefault(c => c.Met);
        bool done = s.UntilAll ? active.All(c => c.Met) : met != null;
        if (done) Complete(s.UntilAll ? "all" : met!.Type switch { "all_clicked" => "targets", var x => x });
    }

    private static void BuildCard(CardModel card, Tutorial? t, int fgPid)
    {
        card.StepVersion = _stepVersion;
        if (t is { OutroOpen: true })
        {
            BuildOutroCard(card, t, fgPid);
            return;
        }
        if (t == null || !t.Active) return;
        var s = t.Current;
        // the card follows the same rule as the marks: only while the user is in the app
        if (t.HideWhenInactive && s != null && !StepActive(t, s, fgPid)) return;
        if (t.HideWhenInactive && s == null && t.Defaults.Process != null && !AppActive(Array.Empty<int>(), t.Defaults.Process, fgPid)) return;
        var appGeo = t.Defaults.Process != null ? AnchorNow(t.Defaults, out _) : null;
        card.Visible = true;
        // menus / dialogs / plugin windows of the app: the card must not cover them either
        var vs = Native.VirtualScreen();
        foreach (var w in Resolver.Popups)
            if (w.Frame.IntersectsWith(vs)) card.Avoid.Add(w.Frame);
        card.State = t.StateName;
        card.Index = t.Index;
        card.Total = t.Steps.Count;
        card.AppFrame = appGeo?.Bounds;
        card.Completed = t.AdvanceAt != null;
        if (t.State == TutorialState.Waiting || s == null)
        {
            card.Title = t.Title.Length > 0 ? t.Title : Settings.Current.Text.Tutorial;
            card.Text = "";
            card.Controls = new HashSet<string> { "stop" };
            // the user is never left before a spinner with no way on: finish now, straight to the end card
            if (t.State == TutorialState.Waiting)
                card.Extra = new List<CardButton> { new("wait_finish", Settings.Current.Text.WaitFinish, "primary", "wait_finish") };
        }
        else
        {
            card.Title = s.Title;
            card.Text = s.Text;
            card.Why = s.Why;
            var req = s.Required.ToList();
            card.Required = req.Count;
            card.Done = req.Count(m => m.Satisfied);
            card.CanBack = t.Index > 0;
            card.CanUndo = s.History.Count > 0;
            if (s.Checklist ?? req.Count(m => m.Label != null) >= 2)
                for (int i = 0; i < req.Count; i++) card.Checklist.Add((req[i].Label ?? string.Format(Settings.Current.Text.Target, i + 1), req[i].Satisfied));
            card.Extra = s.Buttons;
            card.Hint = s.Hint;
            card.HintTitle = s.HintTitle;
            card.HelpPending = s.HelpPending;
            card.Checking = s.Checking;
            card.Highlighting = s.HighlightUntil != null;
            card.Issue = TargetIssue(s);
            card.Review = s.Review && !s.Until.Any(c => c.SeenUnmet && c.Type != "manual");
            card.Controls = s.Controls ?? new HashSet<string> { "back", "undo", "skip", "done", "stop", "help" };
            if (BlockingDialog(s) is string dialog) ShowDialogFirst(card, t, s, dialog);
            NotePrep(t, card.Prep ? BlockingDialog(s) : null);
            var firstVisible = s.Targets.FirstOrDefault(m => m.Geo != null);
            if (firstVisible != null) card.Focus = firstVisible.Geo!.Center;
        }
        if (card.Focus == default && appGeo?.Bounds is RectangleF ab) card.Focus = new PointF(ab.X + ab.Width / 2, ab.Y + ab.Height / 2);
    }

    /// <summary>
    /// The app waits on a dialog the step knows nothing about: the step's own text would describe a screen the user
    /// cannot reach yet, so the card asks to deal with the dialog instead — "let's get ready" before the first step.
    /// </summary>
    private static void ShowDialogFirst(CardModel card, Tutorial t, Step s, string dialog)
    {
        var loc = Settings.Current.Text;
        bool notStarted = t.Index == 0 && s.History.Count == 0 && !s.Targets.Any(m => m.Satisfied);
        card.Prep = notStarted;
        card.Title = notStarted ? loc.PrepareTitle : loc.DialogTitle;
        card.Text = string.Format(loc.PrepareText, dialog);
        card.Why = null;
        card.Issue = null;
        card.Hint = null;
        card.HintTitle = null;
        card.Checklist.Clear();
        card.Extra = new();
        card.Review = false;
        // "done" or "skip" would move the tutorial on while the app is still stuck on the dialog
        card.Controls = new HashSet<string> { "stop", "help" };
    }

    private static (Tutorial t, string dialog)? _prep;

    /// <summary>Step 0 began (a dialog to answer before step 1) or ended: the agent hears about both.</summary>
    private static void NotePrep(Tutorial t, string? dialog)
    {
        if (dialog != null && _prep is not { } p0)
        {
            _prep = (t, dialog);
            Events.Emit("prepare_started", new() { ["dialog"] = dialog, ["note"] = "step 0: the app waits on this dialog; the card asks the user to answer or close it before step 1" });
        }
        else if (dialog == null && _prep is { } p && ReferenceEquals(p.t, t))
        {
            _prep = null;
            Events.Emit("prepare_done", new() { ["dialog"] = p.dialog, ["note"] = "the dialog is gone: step 1 is shown" });
        }
        else if (_prep is { } q && !ReferenceEquals(q.t, t)) _prep = null;
    }

    // =========================================================== sequence

    /// <summary>
    /// Sequence steps show only the first unfinished target. If that target's window vanishes (the context menu
    /// was closed without choosing) the previous click is taken back, so the chain starts over.
    /// </summary>
    private static void ApplySequence(Step s, DateTime now)
    {
        int k = s.Targets.FindIndex(m => !m.Satisfied && !m.Optional);
        for (int i = k + 1; k >= 0 && i < s.Targets.Count; i++)
        {
            s.Targets[i].Geo = null;
            s.Targets[i].Visibility = "later";
        }
        if (k <= 0) { s.SeqMissingSince = null; return; }
        var cur = s.Targets[k];
        var prev = s.Targets[k - 1];
        bool gone = cur.Visibility.Contains("not found") || cur.Visibility is "no visible window" or "closed" or "hidden";
        if (!gone || cur.OpensArmedAt != null || prev.Check != null) { s.SeqMissingSince = null; return; }
        s.SeqMissingSince ??= now;
        if ((now - s.SeqMissingSince.Value).TotalMilliseconds < 1200) return;   // menus take a moment to appear
        s.SeqMissingSince = null;
        prev.Dismissed = false;
        prev.ShownAt = null;
        Events.Emit("sequence_reset", new()
        {
            ["step"] = _tut?.Index, ["from"] = cur.Id, ["back"] = prev.Id, ["reason"] = cur.Visibility,
            ["note"] = "the follow-up target's window disappeared (menu closed?) — the previous target is shown again",
        });
        _stepVersion++;
    }

    // =========================================================== help

    /// <summary>Why the user can't see this step's targets (null when at least one is visible).</summary>
    private static string? TargetIssue(Step s)
    {
        if (BlockingDialog(s) is string dialog) return string.Format(Settings.Current.Text.IssueDialog, dialog);
        var open = s.Targets.Where(m => !m.Satisfied && (m.HasHitArea || m.HasButtons || m.Check != null)).ToList();
        if (open.Count == 0 || open.Any(m => m.Visibility == "visible")) return null;
        var t = Settings.Current.Text;
        var v = open[0].Visibility;
        return v switch
        {
            "minimized" => t.IssueMinimized,
            "scroll-up" => string.Format(t.ScrollIssue, t.ScrollUp.ToLowerInvariant()),
            "scroll-down" => string.Format(t.ScrollIssue, t.ScrollDown.ToLowerInvariant()),
            "scroll-left" => string.Format(t.ScrollIssue, t.ScrollLeft.ToLowerInvariant()),
            "scroll-right" => string.Format(t.ScrollIssue, t.ScrollRight.ToLowerInvariant()),
            "scroll-unknown" or "template not visible" or "text not visible" => t.ScrollFind,
            "occluded" => t.IssueOccluded,
            "inactive" => t.IssueInactive,
            "offscreen" => t.IssueOffscreen,
            "process not running" => t.IssueNotRunning,
            "elevated" => t.IssueElevated,
            "resolving" or "searching" => null,
            _ => t.IssueMissing,
        };
    }

    /// <summary>The user says they can't find the target / don't understand the step.</summary>
    /// <param name="sketch">areas the user circled with the pencil; disposed here</param>
    public static void RequestHelp(string reason, string source, string? text = null, Sketch? sketch = null)
    {
        JsonObject? drawn = null;
        if (sketch != null)
        {
            try { drawn = SketchJson(sketch); }
            finally { sketch.Dispose(); }
        }
        lock (Sync)
        {
            var t = _tut;
            var s = t is { State: TutorialState.Running } ? t.Current : null;
            if (s == null) return;
            s.HelpPending = true;
            s.Sketch = sketch is { Empty: false } ? sketch.Strokes.ToList() : null;
            if (reason == "cant_find") { s.HighlightUntil = DateTime.UtcNow.AddSeconds(6); s.NextPing = default; }
            Native.GetCursorPos(out var cur);
            Events.Emit("help", new()
            {
                ["reason"] = reason, ["source"] = source, ["text"] = text, ["step"] = t!.Index, ["id"] = s.Id, ["title"] = s.Title,
                ["sketch"] = drawn,
                ["targets"] = new JsonArray(s.Targets.Select(m => (JsonNode)MarkJson(m)).ToArray()),
                ["issue"] = TargetIssue(s),
                ["blockingDialog"] = BlockingDialog(s),
                ["recentMisses"] = new JsonArray(s.Misses.Select(x => (JsonNode)x.DeepClone()).ToArray()),
                ["cursor"] = new JsonArray(cur.X, cur.Y),
                ["foreground"] = ForegroundInfo(),
                ["advice"] = reason switch
                {
                    "cant_find" => "targets are being highlighted for 6 s; consider screenshot(includeOverlay) to verify placement, then hint() or re-draw",
                    "other" when drawn != null => "the user described the problem in their own words (text) and circled the area they mean (sketch: " +
                        "bounds/strokes in screen pixels, the UIA element at its centre, and sketch.image — a PNG of that area with the circling, open it with Read); " +
                        "answer with hint(), fix the step, or ask via hint",
                    "other" => "the user described the problem in their own words (text); answer with hint(), fix the step, or ask via hint",
                    _ => "explain the step in simpler words with hint(text), or split it with append_steps/insertAt",
                },
            });
            _stepVersion++;
        }
    }

    /// <summary>The pencil sketch for the agent: where it is, what is under it, and a picture of it.</summary>
    private static JsonObject SketchJson(Sketch sk)
    {
        var b = sk.Bounds();
        var c = new PointF(b.X + b.Width / 2, b.Y + b.Height / 2);
        // offsets against the step's first target, so a misplaced mark can be moved to where the user points
        var anchor = _tut?.Current?.Targets.FirstOrDefault()?.Anchor;
        JsonObject at;
        try { at = PointInfo(c, anchor, out _); }
        catch (Exception ex) { at = new JsonObject { ["x"] = (int)c.X, ["y"] = (int)c.Y, ["error"] = ex.Message }; }
        return new JsonObject
        {
            ["bounds"] = J.RectJson(b),
            ["center"] = J.PtJson(c),
            ["strokes"] = new JsonArray(sk.Strokes.Select(s => (JsonNode)StrokeJson(s)).ToArray()),
            ["atCenter"] = at,
            ["image"] = sk.SaveImage(),
        };
    }

    private static JsonObject StrokeJson(PointF[] pts)
    {
        float l = pts.Min(p => p.X), t = pts.Min(p => p.Y), r = pts.Max(p => p.X), btm = pts.Max(p => p.Y);
        float size = Math.Max(r - l, btm - t);
        float gap = MathF.Sqrt((pts[0].X - pts[^1].X) * (pts[0].X - pts[^1].X) + (pts[0].Y - pts[^1].Y) * (pts[0].Y - pts[^1].Y));
        int step = Math.Max(1, (int)Math.Ceiling(pts.Length / 48.0));
        var kept = pts.Where((_, i) => i % step == 0 || i == pts.Length - 1);
        return new JsonObject
        {
            ["bounds"] = J.RectJson(RectangleF.FromLTRB(l, t, r, btm)),
            // a loop around something vs. a line / underline / pointer
            ["shape"] = size < 12 ? "dot" : gap < size * 0.35f ? "loop" : "line",
            ["from"] = J.PtJson(pts[0]), ["to"] = J.PtJson(pts[^1]),
            ["points"] = new JsonArray(kept.Select(p => (JsonNode)new JsonArray((int)p.X, (int)p.Y)).ToArray()),
        };
    }

    private static void AddSketchVisuals(List<PointF[]> strokes, Scene sc, List<RectangleF> avoid)
    {
        foreach (var pts in strokes.Where(p => p.Length > 0))
        {
            float k = Native.ScaleAt(pts[0]);
            var b = RectangleF.FromLTRB(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
            sc.Prims.Add((new Prim { Kind = PrimKind.Stroke, Points = pts, Rect = b, Color = Palette.Accent, Width = 4 * k, Scale = k }, 1f));
            avoid.Add(b);
        }
    }

    /// <summary>The agent's answer on the card (cleared when the step changes).</summary>
    public static JsonObject SetHint(JsonNode args)
    {
        lock (Sync)
        {
            var t = RequireOwned();
            if (J.Int(args, "expectIndex") is int e && e != t.Index) throw new InvalidOperationException($"current step is {t.Index}, not {e}");
            var s = t.Current ?? throw new InvalidOperationException("no current step");
            s.Hint = J.Str(args, "text");
            s.HintTitle = J.Str(args, "title");
            s.HelpPending = false;
            s.Sketch = null;
            if (J.Bool(args, "highlight") == true) { s.HighlightUntil = DateTime.UtcNow.AddSeconds(J.Int(args, "seconds") ?? 6); s.NextPing = default; }
            _stepVersion++;
            return TutorialJson();
        }
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
                ["card"] = CardRect is Rectangle cr ? J.RectJson(cr) : null,
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
            ["state"] = t.StateName, ["title"] = t.Title, ["index"] = t.Index, ["total"] = t.Steps.Count,
            ["app"] = t.Defaults.Process, ["completing"] = t.AdvanceAt != null, ["owner"] = OwnerLabel(t),
        };
        var s = t.Current;
        if (s != null && t.Active)
            o["step"] = new JsonObject
            {
                ["id"] = s.Id, ["title"] = s.Title, ["text"] = s.Text, ["checking"] = s.Checking ? true : null,
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
            // a clicked-away mark is resolved and on screen, but nothing is drawn for it: say so
            ["id"] = m.Id, ["name"] = m.Label, ["anchor"] = m.Anchor.Describe(), ["visibility"] = m.Dismissed && m.Visibility == "visible" ? "dismissed" : m.Visibility,
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
